import json
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import MagicMock, patch

import whatsapp_reader as reader
import whatsapp_runtime as runtime
from whatsapp_dom import WebUI, parse_time


def record(chat='123@g.us', message='m1', text='Pedido 3213 confirmado'):
    return dict(chat_id=chat, chat='Equipe', chat_type='group' if chat.endswith('@g.us') else 'contact',
                message_id=message, text=text, timestamp='2026-11-03T14:00:00-03:00', time='14:00, 03/11/2026',
                sender='Ana', direction='incoming', reference='M'+runtime.digest(chat+'|'+message)[:16],
                source_verified=True, fetched_at='2026-11-04T12:00:00+00:00')


class RuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.profile = str(Path(self.temp.name)/'profile')
        self.cache = str(Path(self.temp.name)/'cache.db')
        self.patches = [patch.object(reader,'PROFILE_DIR',self.profile),patch.object(reader,'CACHE_DB_PATH',self.cache)]
        for p in self.patches:p.start()
        runtime.init_cache()

    def tearDown(self):
        for p in reversed(self.patches):p.stop()
        self.temp.cleanup()

    def test_daily_group_read_returns_dated_verified_messages_and_partial_scope(self):
        session = runtime.ReaderSession()
        session.driver = MagicMock()
        session.driver.execute_script.return_value = [{"name": "Equipe", "is_group": True}]
        ui = MagicMock()
        ui.open_chat.return_value = {"chat": "Equipe", "chat_type": "group"}
        ui.messages.return_value = [record(), dict(record(message="old"), timestamp="2026-11-02T15:00:00-03:00")]
        with patch.object(session, "connect"), patch.object(runtime, "WebUI", return_value=ui), patch.object(reader, "select_groups_filter"):
            result = session.execute(dict(operation="read_recent_groups", chat_type="group",
                date_from="2026-11-03T00:00:00-03:00", date_to="2026-11-04T00:00:00-03:00"))
        self.assertEqual(result["status"], "partial")
        self.assertEqual(result["coverage"]["groups_read"], 1)
        self.assertEqual(result["coverage"]["date_from"], "2026-11-03T00:00:00-03:00")
        self.assertEqual([m["message_id"] for m in result["messages"]], ["m1"])
        ui.scroll.assert_not_called()

    def test_group_overview_scrolls_back_until_the_requested_day(self):
        def message(mid, stamp):
            return dict(record(message=mid), timestamp=stamp)
        today = [message("t%d" % i, "2026-11-04T1%d:00:00-03:00" % i) for i in range(3)]
        yesterday = [message("y1", "2026-11-03T16:00:00-03:00"), message("y2", "2026-11-03T09:00:00-03:00")]
        older = [message("o1", "2026-11-02T18:00:00-03:00")]
        session = runtime.ReaderSession()
        session.driver = MagicMock()
        session.driver.execute_script.return_value = [{"name": "Equipe", "is_group": True}]
        ui = MagicMock()
        ui.open_chat.return_value = {"chat": "Equipe", "chat_type": "group"}
        ui.messages.side_effect = [today, yesterday + today, older + yesterday + today]
        ui.scroll.return_value = {"moved": True, "boundary": False}
        with patch.object(session, "connect"), patch.object(runtime, "WebUI", return_value=ui), patch.object(reader, "select_groups_filter"):
            result = session.execute(dict(operation="read_recent_groups", chat_type="group",
                date_from="2026-11-03T00:00:00-03:00", date_to="2026-11-04T00:00:00-03:00"))
        self.assertEqual([m["message_id"] for m in result["messages"]], ["y1", "y2"])
        self.assertEqual(ui.scroll.call_count, 2)
        ui.scroll.assert_called_with("#main", -1)

    def test_group_overview_stops_scrolling_at_history_start(self):
        session = runtime.ReaderSession()
        session.driver = MagicMock()
        session.driver.execute_script.return_value = [{"name": "Equipe", "is_group": True}]
        ui = MagicMock()
        ui.open_chat.return_value = {"chat": "Equipe", "chat_type": "group"}
        ui.messages.return_value = [dict(record(), timestamp="2026-11-04T10:00:00-03:00")]
        ui.scroll.return_value = {"moved": False, "boundary": True}
        with patch.object(session, "connect"), patch.object(runtime, "WebUI", return_value=ui), patch.object(reader, "select_groups_filter"):
            result = session.execute(dict(operation="read_recent_groups", chat_type="group",
                date_from="2026-11-03T00:00:00-03:00", date_to="2026-11-04T00:00:00-03:00"))
        self.assertEqual(result["messages"], [])
        self.assertEqual(ui.scroll.call_count, 3)

    def test_group_overview_respects_the_per_group_time_budget(self):
        session = runtime.ReaderSession()
        session.driver = MagicMock()
        session.driver.execute_script.return_value = [{"name": "Equipe", "is_group": True}]
        ui = MagicMock()
        ui.open_chat.return_value = {"chat": "Equipe", "chat_type": "group"}
        ui.messages.return_value = [dict(record(), timestamp="2026-11-04T10:00:00-03:00")]
        ui.scroll.return_value = {"moved": True, "boundary": False}
        with patch.object(session, "connect"), patch.object(runtime, "WebUI", return_value=ui), \
                patch.object(reader, "select_groups_filter"), patch.object(runtime, "RECENT_GROUP_SECONDS", 1.5):
            result = session.execute(dict(operation="read_recent_groups", chat_type="group",
                date_from="2026-11-03T00:00:00-03:00", date_to="2026-11-04T00:00:00-03:00"))
        self.assertEqual(result["coverage"]["groups_read"], 1)
        self.assertLessEqual(ui.scroll.call_count, 4)

    def test_custom_lists_use_known_group_names_but_read_live_messages(self):
        runtime.save_records([record(message="cached")])
        session = runtime.ReaderSession()
        session.driver = MagicMock()
        session.driver.execute_script.return_value = [{"name":"Contato"}, {"name":"Equipe"}]
        ui = MagicMock()
        ui.open_chat.return_value = {"chat":"Equipe", "chat_type":"group"}
        ui.messages.return_value = [record(message="live")]
        ui.scroll.return_value = {"moved": False, "boundary": True}
        with patch.object(session, "connect"), patch.object(runtime, "WebUI", return_value=ui), patch.object(reader, "select_groups_filter", side_effect=reader.ReaderFailure("dom_changed","sem filtro")):
            result = session.execute(dict(operation="read_recent_groups", chat_type="group",
                date_from="2026-11-03T00:00:00-03:00", date_to="2026-11-04T00:00:00-03:00"))
        ui.open_chat.assert_called_once_with("Equipe", kind="group")
        self.assertEqual([m["message_id"] for m in result["messages"]], ["live"])
        self.assertIn("visible_known_groups", result["coverage"]["issues"])

    def test_migration_preserves_legacy_and_is_idempotent(self):
        reader.save_messages_to_cache('Equipe',[{'text':'3213'}])
        with reader.cache_connection() as c:c.execute('PRAGMA user_version=0')
        runtime.init_cache();runtime.init_cache()
        self.assertEqual(len(list(Path(self.temp.name).glob('*.bak'))),1)
        self.assertEqual(reader.query_cached_messages(keyword='3213')[0]['text'],'3213')

    def test_fts_accent_and_duplicate_update(self):
        item=record(text='Reunião 3213');runtime.save_records([item,item])
        self.assertEqual(len(runtime.query_cache({'query':'reuniao','limit':20})),1)
        item['text']='3213 alterado';runtime.save_records([item])
        self.assertEqual(runtime.query_cache({'query':'reuniao'}),[])

    def test_homonyms_have_distinct_ids(self):
        runtime.save_records([record('1@g.us'),record('2@g.us')])
        self.assertEqual(len(runtime.query_cache({'query':'3213'})),2)
        self.assertEqual(len(runtime.query_cache({'query':'3213','chat_id':'1@g.us'})),1)

    def test_source_identity_required(self):
        item=record();item['source_verified']=False
        with self.assertRaises(runtime.ReaderFailure):runtime.save_records([item])

    def test_cache_dates_compare_instants_across_offsets_and_respect_order(self):
        first=record(message='early');first['timestamp']='2026-11-04T00:30:00+00:00'
        second=record(message='later');second['timestamp']='2026-11-03T23:00:00-03:00'
        runtime.save_records([first,second])
        request={'query':'3213','date_from':'2026-11-03T20:00:00-03:00','date_to':'2026-11-04T00:00:00-03:00','order':'oldest'}
        self.assertEqual([m['message_id'] for m in runtime.query_cache(request)],['early','later'])
        request['date_from']='2026-11-03T22:00:00-03:00'
        self.assertEqual([m['message_id'] for m in runtime.query_cache(request)],['later'])

    def test_numeric_identifier_is_not_a_substring_of_another_identifier(self):
        self.assertTrue(runtime.matches_request(record(text='Reforma 3213 e 3225'),{'query':'3213'}))
        self.assertFalse(runtime.matches_request(record(text='Reforma 13213'),{'query':'3213'}))
        self.assertFalse(runtime.matches_request(record(text='Reforma 32130'),{'query':'3213'}))

    def test_cache_works_while_profile_locked(self):
        runtime.save_records([record()])
        with reader.ProfileLock(self.profile), patch.object(reader,'get_driver',side_effect=AssertionError('browser used')):
            result=runtime.ReaderSession().execute({'operation':'search_cache','query':'3213'})
        self.assertEqual(result['status'],'cached');self.assertEqual(len(result['results']),1)

    def test_dates_and_type_filter(self):
        runtime.save_records([record(),record('1@c.us')])
        self.assertEqual(len(runtime.query_cache({'query':'3213','chat_type':'contact'})),1)
        self.assertEqual(runtime.query_cache({'query':'3213','date_from':'2026-11-04T00:00:00-03:00'}),[])

    def test_cursor_rejects_other_query_and_survives_session(self):
        request=runtime.validate_request({'operation':'search','query':'3213'})
        token=runtime.save_cursor(request,{'seen':['M1'],'rows':['r1'],'offset':3})
        request['cursor']=token
        self.assertEqual(runtime.load_cursor(request)['seen'],['M1'])
        request['query']='999'
        with self.assertRaises(runtime.ReaderFailure):runtime.load_cursor(request)

    def test_date_validation_and_exclusive_end(self):
        with self.assertRaises(runtime.ReaderFailure):runtime.validate_request({'operation':'search','query':'x','date_from':'2026-11-04'})
        self.assertFalse(runtime.matches_request(record(),{'date_to':'2026-11-03T14:00:00-03:00'}))
        self.assertEqual(parse_time('14:02, 03/11/2026'),'2026-11-03T14:02:00-03:00')

    def test_missing_query_is_error_not_listing(self):
        result=runtime.ReaderSession().execute({'operation':'search','query':''})
        self.assertEqual(result['status'],'missing_query')

    def test_worker_reuses_driver_and_releases_profile(self):
        driver=MagicMock()
        with patch.object(reader,'get_driver',return_value=driver) as start,patch.object(reader,'check_login_status',return_value='LOGGED_IN'):
            session=runtime.ReaderSession()
            for i in range(3):self.assertEqual(session.execute({'operation':'status'})['status'],'LOGGED_IN')
            session.close()
            start.assert_called_once();driver.quit.assert_called_once()
        with reader.ProfileLock(self.profile):pass

    def test_embedded_pairing_renews_qr_and_confirms_same_profile(self):
        events = []
        driver = MagicMock()
        frames = iter(['QR_TEST_A', 'QR_TEST_A', 'QR_TEST_B'])
        class Qr:
            @property
            def screenshot_as_base64(self):
                return next(frames)
        checks = 0
        def elements(kind, selector):
            nonlocal checks
            if selector == 'pane-side':
                checks += 1
                return [object()] if checks >= 4 else []
            return [Qr()]
        driver.find_elements.side_effect = elements
        with patch.object(reader, 'get_driver', return_value=driver) as start, patch.object(reader, 'check_login_status', return_value='NEEDS_QR'), patch.object(runtime.time, 'sleep'):
            session = runtime.ReaderSession(emit=events.append)
            result = session.execute({'operation': 'pair', 'embedded': True, 'request_id': 'pair-test'})
            session.close()
        self.assertTrue(result['authenticated'])
        start.assert_called_once_with(headless=True)
        qr_events = [event for event in events if event['event'] == 'qr']
        self.assertEqual([event['png_base64'] for event in qr_events], ['QR_TEST_A', 'QR_TEST_B'])
        self.assertTrue(all(event['request_id'] == 'pair-test' for event in qr_events))
        for file in Path(self.temp.name).rglob('*'):
            if file.is_file():
                self.assertNotIn(b'QR_TEST_', file.read_bytes())

    def test_embedded_pairing_cancel_returns_cancelled_and_releases_profile(self):
        cancel = threading.Event()
        driver = MagicMock()
        driver.find_elements.side_effect = lambda kind, selector: [] if selector == 'pane-side' else [MagicMock(screenshot_as_base64='QR_CANCEL_TEST')]
        def emit(event):
            if event['event'] == 'qr':
                cancel.set()
        with patch.object(reader, 'get_driver', return_value=driver), patch.object(reader, 'check_login_status', return_value='NEEDS_QR'), patch.object(runtime.time, 'sleep'):
            session = runtime.ReaderSession(emit=emit, cancel=cancel)
            result = session.execute({'operation': 'pair', 'embedded': True})
            session.close()
        self.assertEqual(result['status'], 'user_cancelled')
        with reader.ProfileLock(self.profile):
            pass

    def test_embedded_pairing_expiry_is_not_connected(self):
        with patch.object(reader, 'get_driver', return_value=MagicMock()), patch.object(reader, 'check_login_status', return_value='NEEDS_QR'), patch.object(runtime, 'PAIR_SECONDS', 0):
            session = runtime.ReaderSession()
            result = session.execute({'operation': 'pair', 'embedded': True})
            session.close()
        self.assertEqual(result['status'], 'timeout')
        self.assertFalse(result.get('authenticated', False))

    def test_auth_failure_is_not_empty_success(self):
        with patch.object(reader,'get_driver',return_value=MagicMock()),patch.object(reader,'check_login_status',return_value='NEEDS_QR'):
            session=runtime.ReaderSession();result=session.execute({'operation':'search','query':'3213'});session.close()
        self.assertEqual(result['status'],'needs_auth');self.assertEqual(result['stage'],'authentication')

    def test_cancellation_before_collection(self):
        cancel=threading.Event();cancel.set()
        with self.assertRaises(runtime.ReaderFailure) as failure:runtime.ReaderSession(cancel=cancel).checkpoint()
        self.assertEqual(failure.exception.status,'user_cancelled')

    def test_global_search_paginates_and_deduplicates(self):
        driver=MagicMock();ui=MagicMock();items=[record(message='a'),record(message='b')]
        rows=[[{'key':'a','name':'Equipe','element':MagicMock()}],[{'key':'b','name':'Equipe','element':MagicMock()}]]
        step=[0]
        def execute(script,*args):
            if script==runtime.SEARCH_ROWS_JS:return rows[min(step[0],1)]
            return []
        driver.execute_script.side_effect=execute
        ui.chat_identity.return_value={'chat_id':'123@g.us','chat':'Equipe','chat_type':'group','source_verified':True}
        ui.sync_in_progress.return_value=False
        ui.messages.side_effect=lambda identity:[items[min(step[0],1)]]
        def scroll(*args):
            step[0]+=1
            return {'moved':step[0]==1,'boundary':step[0]>1}
        ui.scroll.side_effect=scroll
        events=[];session=runtime.ReaderSession(events.append);session.driver=driver
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            result=session.collect(runtime.validate_request({'operation':'search','query':'3213','limit':1}))
        self.assertEqual(len(result['results']),2)
        self.assertTrue(result['coverage']['complete'])
        self.assertEqual(len([x for x in events if x['event']=='batch']),2)

    def test_result_identity_failure_makes_coverage_partial(self):
        driver=MagicMock();ui=MagicMock()
        driver.execute_script.return_value=[{'key':'a','name':'Equipe','element':MagicMock()}]
        ui.chat_identity.side_effect=runtime.ReaderFailure('chat_mismatch','wrong chat')
        ui.sync_in_progress.return_value=False
        ui.scroll.return_value={'moved':False,'boundary':True}
        session=runtime.ReaderSession();session.driver=driver
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            result=session.collect(runtime.validate_request({'operation':'search','query':'3213'}))
        self.assertEqual(result['status'],'partial');self.assertEqual(result['results'],[])
        self.assertIn('chat_mismatch',result['coverage']['issues']);self.assertTrue(result['cursor'])

    def test_search_scrolls_through_empty_virtualized_viewport(self):
        driver=MagicMock();ui=MagicMock();step=[0]
        rows=[[],[{'key':'target','message_id':'target','name':'Equipe','element':MagicMock()}]]
        driver.execute_script.side_effect=lambda script,*args:rows[min(step[0],1)] if script==runtime.SEARCH_ROWS_JS else []
        ui.empty_state.return_value=False;ui.search_active.return_value=True;ui.sync_in_progress.return_value=False
        ui.chat_identity.return_value={'chat_id':'123@g.us','chat':'Equipe','chat_type':'group','source_verified':True}
        ui.messages.return_value=[record(message='target')]
        def scroll(*args):
            step[0]+=1
            return {'moved':step[0]==1,'boundary':step[0]>1}
        ui.scroll.side_effect=scroll
        session=runtime.ReaderSession();session.driver=driver
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            result=session.collect(runtime.validate_request({'operation':'search','query':'3213'}))
        self.assertEqual(result['status'],'success')
        self.assertEqual([m['message_id'] for m in result['results']],['target'])

    def test_chat_cursor_resumes_with_identity_learned_from_first_response(self):
        # The caller only learns chat_id from the first response; resume must still match.
        ui=MagicMock();ui.open_chat.return_value={'chat_id':'ui:abc','chat':'Custos de Reforma  - Dono Paulo','chat_type':'group','source_verified':True}
        ui.chat_identity.return_value=ui.open_chat.return_value;ui.sync_in_progress.return_value=False
        ui.search_active.return_value=True;ui.empty_state.return_value=False
        ui.scroll.side_effect=runtime.ReaderFailure('collection_deadline','limite')
        driver=MagicMock()
        session=runtime.ReaderSession();session.driver=driver
        first=runtime.validate_request({'operation':'search','query':'3213','chat_name':'custos de reforma'})
        hit=record('ui:abc','m1');hit.update(ui.open_chat.return_value);ui.messages.return_value=[hit]
        driver.execute_script.return_value=[{'key':'m1','message_id':'m1','name':'','element':MagicMock()}]
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            result=session.collect(first)
        self.assertTrue(result['cursor'])
        resume=runtime.validate_request({'operation':'search','query':'3213','chat_name':'custos de reforma','chat_id':'ui:abc','cursor':result['cursor']})
        self.assertEqual(runtime.load_cursor(resume)['seen'],[result['results'][0]['reference']])
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            resumed=session.collect(resume)
        ui.open_chat.assert_called_with('custos de reforma','ui:abc','all',0)
        self.assertEqual(resumed['results'],[])  # already-delivered message is not repeated

    def test_partial_chat_name_filters_by_resolved_identity(self):
        item=record('ui:abc');item['chat']='Custos de Reforma  - Dono Paulo'
        self.assertTrue(runtime.matches_request(item,{'query':'3213','chat_id':'ui:abc','chat_name':'Custos de Reforma  - Dono Paulo'}))
        self.assertFalse(runtime.matches_request(item,{'query':'3213','chat_id':'ui:other'}))

    def test_v3_migration_drops_unstable_anchor_identities_only(self):
        stable=record('ui:stable','m1');unstable=record('ui:anchor','m2');unstable['identity_scope']='message_anchor'
        runtime.save_records([stable,unstable])
        with reader.cache_connection() as c:c.execute('PRAGMA user_version=2')
        runtime.init_cache()
        self.assertEqual([m['message_id'] for m in runtime.query_cache({'query':'3213'})],['m1'])
        self.assertEqual(len(list(Path(self.temp.name).glob('*.pre-v3-*.bak'))),1)

    def test_chat_candidate_validation(self):
        self.assertEqual(runtime.validate_request({'operation':'search','query':'x','chat_name':'PCP','chat_candidate':'2'})['chat_candidate'],2)
        for bad in ({'chat_name':'PCP','chat_candidate':'dois'},{'chat_name':'PCP','chat_candidate':30},{'chat_candidate':1}):
            with self.assertRaises(runtime.ReaderFailure):runtime.validate_request(dict({'operation':'search','query':'x'},**bad))

    def test_global_hits_from_homonymous_names_are_flagged_not_attributed(self):
        ui=MagicMock();ui.find_chats.side_effect=lambda name:[{'exact':True},{'exact':True}] if name=='João' else [{'exact':True}]
        a=record('ui:j','m1');a['chat']='João';a['chat_type']='contact'
        b=record('ui:e','m2')
        warnings=runtime.ReaderSession().check_homonyms(ui,[a,b])
        self.assertEqual([w['chat'] for w in warnings if w['status']=='homonymous'],['João'])
        self.assertEqual(a['identity_scope'],'ambiguous_display_name');self.assertNotIn('identity_scope',b)

    def test_open_chat_resolves_partial_ambiguous_and_numbered_choice(self):
        ui=WebUI(MagicMock(),runtime.ReaderFailure,lambda:None,lambda *a:None)
        rows=[{'name':'DEMANDAS PCP','preview':'p1','self':False,'exact':False},{'name':'Mônica PCP','preview':'p2','self':False,'exact':False}]
        opened=[]
        def open_row(row,name,kind,discriminate=False):
            opened.append((name,discriminate));return {'chat_id':'ui:'+name,'chat':name,'chat_type':'group','source_verified':True,'identity_scope':'display_name'}
        with patch.object(WebUI,'find_chats',return_value=rows),patch.object(WebUI,'open_row',side_effect=open_row):
            with self.assertRaises(runtime.ReaderFailure) as failure:ui.open_chat('PCP')
            self.assertEqual(failure.exception.status,'ambiguous_chat')
            self.assertEqual([m['candidate'] for m in failure.exception.data['matches']],[1,2])
            self.assertEqual(ui.open_chat('PCP',candidate=2)['chat'],'Mônica PCP')
            self.assertEqual(ui.open_chat('PCP',chat_id='ui:Mônica PCP')['chat'],'Mônica PCP')  # resume by id
            with self.assertRaises(runtime.ReaderFailure):ui.open_chat('PCP',candidate=3)
        with patch.object(WebUI,'find_chats',return_value=rows[:1]),patch.object(WebUI,'open_row',side_effect=open_row):
            self.assertEqual(ui.open_chat('demandas')['chat'],'DEMANDAS PCP')
        twins=[dict(rows[1],exact=True),dict(rows[1],exact=True,self=True)]
        with patch.object(WebUI,'find_chats',return_value=twins),patch.object(WebUI,'open_row',side_effect=open_row):
            ui.open_chat('Mônica PCP',candidate=2)
        self.assertEqual(opened[-1],('Mônica PCP',True))  # true homonyms need a profile discriminator
        with patch.object(WebUI,'find_chats',return_value=[rows[0]]+twins),patch.object(WebUI,'open_row',side_effect=open_row):
            ui.open_chat('PCP',candidate=3)
        self.assertEqual(opened[-1],('Mônica PCP',True))  # twins inside a partial list too

    def test_resume_by_name_based_id_opens_the_likely_chat_first(self):
        ui=WebUI(MagicMock(),runtime.ReaderFailure,lambda:None,lambda *a:None)
        rows=[{'name':'DEMANDAS PCP','preview':'p1','self':False},{'name':'Mônica PCP','preview':'p2','self':False}]
        saved='ui:'+runtime.digest(runtime.normalized('Mônica PCP')+'|contact')
        opened=[]
        def open_row(row,name,kind,discriminate=False):
            opened.append(name);return {'chat_id':'ui:'+runtime.digest(runtime.normalized(name)+'|contact'),'chat':name,'chat_type':'contact'}
        with patch.object(WebUI,'find_chats',return_value=rows),patch.object(WebUI,'open_row',side_effect=open_row):
            self.assertEqual(ui.open_chat('PCP',chat_id=saved)['chat'],'Mônica PCP')
        self.assertEqual(opened,['Mônica PCP'])

    def test_divider_dates_resolve_in_sao_paulo(self):
        from whatsapp_dom import resolve_day, SAO_PAULO
        from datetime import datetime
        now=datetime(2026,11,4,21,0,tzinfo=SAO_PAULO)  # quarta-feira
        self.assertEqual(resolve_day('Hoje','16:58',now),'2026-11-04T16:58:00-03:00')
        self.assertEqual(resolve_day('Ontem','08:05',now),'2026-11-03T08:05:00-03:00')
        self.assertEqual(resolve_day('sexta-feira','10:00',now),'2026-10-30T10:00:00-03:00')
        self.assertEqual(resolve_day('quarta-feira','10:00',now),'2026-10-28T10:00:00-03:00')  # a week ago, never today
        self.assertEqual(resolve_day('28/09/2026','07:30',now),'2026-09-28T07:30:00-03:00')
        self.assertIsNone(resolve_day('','16:58',now));self.assertIsNone(resolve_day('Hoje','',now))

    def test_real_side_panel_sync_never_confirms_absence(self):
        driver=MagicMock();driver.execute_script.return_value=[]
        ui=MagicMock();ui.sync_in_progress.return_value=True;ui.empty_state.return_value=True;ui.search_active.return_value=True
        session=runtime.ReaderSession();session.driver=driver
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            result=session.collect(runtime.validate_request({'operation':'search','query':'5W2H'}))
        self.assertEqual((result['status'],result['coverage']['reason'],result['cursor']),('partial','history_sync_in_progress',None))
        ui.sync_in_progress.return_value=False
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            done=session.collect(runtime.validate_request({'operation':'search','query':'zzqxv'}))
        self.assertEqual((done['status'],done['coverage']['reason']),('success','confirmed_empty'))

    def test_empty_chat_search_is_double_checked_against_loaded_history(self):
        # Observed: "Nenhuma mensagem encontrada" in the chat search for a message on screen.
        driver=MagicMock();driver.execute_script.return_value=[]
        ui=MagicMock();ui.empty_state.return_value=True;ui.search_active.return_value=True;ui.sync_in_progress.return_value=False
        ui.open_chat.return_value={'chat_id':'ui:k','chat':'Kanban','chat_type':'group','source_verified':True}
        ui.scroll.return_value={'moved':False,'boundary':True}
        hit=record('ui:k','m5',text='5W2H Kanban Pokayoke');hit.update(ui.open_chat.return_value);ui.messages.return_value=[hit]
        session=runtime.ReaderSession();session.driver=driver
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            found=session.collect(runtime.validate_request({'operation':'search','query':'5W2H Kanban','chat_name':'Kanban'}))
            absent=session.collect(runtime.validate_request({'operation':'search','query':'zzqxv','chat_name':'Kanban'}))
        self.assertEqual([m['message_id'] for m in found['results']],['m5'])
        self.assertEqual((found['status'],found['coverage']['reason']),('partial','search_index_missed_messages'))
        self.assertEqual((absent['status'],absent['coverage']['reason'],absent['results']),('success','confirmed_empty',[]))
        # The permanent "Sincronizando mensagens mais antigas" chat-search footer is never consulted.
        ui.sync_in_progress.assert_not_called()

    def test_failed_hit_is_not_retried_on_every_page(self):
        driver=MagicMock();ui=MagicMock();clicks=[0];step=[0]
        element=MagicMock();element.click.side_effect=lambda:clicks.__setitem__(0,clicks[0]+1)
        driver.execute_script.side_effect=lambda script,*a:[{'key':'bad','message_id':'bad','name':'Equipe','element':element}] if script==runtime.SEARCH_ROWS_JS else []
        ui.sync_in_progress.return_value=False;ui.empty_state.return_value=False;ui.search_active.return_value=True
        ui.wait.side_effect=runtime.ReaderFailure('dom_changed','nao carregou')
        def scroll(*a):
            step[0]+=1;return {'moved':step[0]<3,'boundary':step[0]>=3}
        ui.scroll.side_effect=scroll
        session=runtime.ReaderSession();session.driver=driver
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            result=session.collect(runtime.validate_request({'operation':'search','query':'3213'}))
        self.assertEqual(clicks[0],1)
        self.assertEqual(result['status'],'partial');self.assertIn('dom_changed',result['coverage']['issues'])

    def test_jsonl_accepts_dotnet_bom_and_survives_malformed_bytes(self):
        # Observed with Console.InputEncoding=utf-8: .NET writes EF BB BF before the first line.
        env=dict(os.environ,FRIDA_WHATSAPP_CACHE=self.cache,FRIDA_WHATSAPP_PROFILE=self.profile,PYTHONIOENCODING='utf-8')
        payload=b'\xef\xbb\xbf'+json.dumps({'request_id':'a','operation':'search_cache','query':'reunião'},ensure_ascii=False).encode('utf-8')+b'\r\n'
        payload+=b'{"request_id":"b","operation":"search_cache","query":"M\xf4nica"}\n'  # cp1252 bytes
        payload+=json.dumps({'request_id':'c','operation':'search_cache','query':'Mônica'},ensure_ascii=False).encode('utf-8')+b'\n'
        result=subprocess.run([sys.executable,str(Path(reader.__file__)),'--serve'],input=payload,capture_output=True,env=env,timeout=10)
        rows=[json.loads(x) for x in result.stdout.decode('utf-8').splitlines()]
        self.assertEqual([x['request_id'] for x in rows if x['event']=='result'],['a','c'])
        self.assertEqual(len([x for x in rows if x['event']=='protocol_error']),1)

    def test_jsonl_correlates_requests_and_one_terminal_each(self):
        env=dict(os.environ,FRIDA_WHATSAPP_CACHE=self.cache,FRIDA_WHATSAPP_PROFILE=self.profile,PYTHONIOENCODING='utf-8')
        requests=[{'request_id':str(i),'operation':'search_cache','query':'3213'} for i in range(2)]
        result=subprocess.run([sys.executable,str(Path(reader.__file__)),'--serve'],input='\n'.join(map(json.dumps,requests))+'\n',capture_output=True,text=True,encoding='utf-8',env=env,timeout=10)
        rows=[json.loads(x) for x in result.stdout.splitlines()]
        terminal=[x for x in rows if x['event']=='result']
        self.assertEqual([x['request_id'] for x in terminal],['0','1']);self.assertTrue(all(x['result']['status']=='cached' for x in terminal))

    def test_query_chat_rows_succeeds_without_headings_when_chat_rows_match(self):
        driver=MagicMock()
        box=MagicMock()
        driver.execute_script.return_value={
            'headings':[],
            'rows':[{'name':'Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON','preview':'17:02','section':'','self':False,'element':MagicMock()}]
        }
        ui=WebUI(driver,runtime.ReaderFailure,lambda:None,lambda *a:None)
        with patch.object(WebUI,'visible',return_value=[box]),patch.object(WebUI,'search_active',return_value=True),patch.object(WebUI,'_clear_box'),patch.object(WebUI,'empty_state',return_value=False),patch.object(WebUI,'reset_search'):
            matches=ui._query_chat_rows('qualidade corte e dobra')
        self.assertEqual(len(matches),1)
        self.assertEqual(matches[0]['name'],'Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON')

    def test_find_chats_token_overlap_resolves_laser_to_corte_e_dobra(self):
        driver=MagicMock()
        ui=WebUI(driver,runtime.ReaderFailure,lambda:None,lambda *a:None)
        row={'name':'Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON','preview':'17:02','section':'conversas','self':False,'element':MagicMock()}
        def fake_query(q):
            return [dict(row)] if q=='qualidade corte' else []
        with patch.object(WebUI,'_query_chat_rows',side_effect=fake_query):
            matches=ui.find_chats('qualidade corte a laser')
        self.assertEqual(len(matches),1)
        self.assertEqual(matches[0]['name'],'Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON')

    def test_query_cache_resolves_partial_group_name_and_empty_query_search_cache(self):
        item=record('999@g.us','m99','Entregues 45 pecas hoje')
        item['chat']='Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON'
        runtime.save_records([item])
        req=runtime.validate_request({'operation':'search_cache','chat_name':'qualidade corte e dobra','query':'','limit':20})
        cached=runtime.query_cache(req)
        self.assertEqual(len(cached),1)
        self.assertEqual(cached[0]['message_id'],'m99')

    def test_read_group_merges_verified_cache_records_from_same_day(self):
        early=record('ui:qcd','m_early','Dobradeira\nItem: ref do trilo do assoalho\nQuantidade: 86 peças')
        early['chat']='Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON'
        early['chat_type']='group'
        early['timestamp']='2026-11-04T16:52:00-03:00'
        runtime.save_records([early])
        late=record('ui:qcd','m_late','Dobradeira\nItem: D.I\nQuantidade: 19 peças')
        late['chat']='Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON'
        late['chat_type']='group'
        late['timestamp']='2026-11-04T17:32:00-03:00'
        driver=MagicMock();ui=MagicMock()
        ui.open_chat.return_value={'chat_id':'ui:qcd','chat':'Qualidade Corte e Dobra - DONO SUEIDE VICE AILTON','chat_type':'group','source_verified':True}
        ui.messages.return_value=[late]
        ui.scroll.return_value={'moved':False,'boundary':True}
        session=runtime.ReaderSession();session.driver=driver
        req=runtime.validate_request({'operation':'read_group','chat_name':'qualidade corte e dobra','date_from':'2026-11-04T00:00:00-03:00','date_to':'2026-11-05T00:00:00-03:00','limit':50})
        with patch.object(runtime,'WebUI',return_value=ui),patch.object(runtime.time,'sleep'):
            res=session.collect(req)
        self.assertEqual({m['message_id'] for m in res['messages']},{'m_early','m_late'})


if __name__=='__main__':unittest.main()
