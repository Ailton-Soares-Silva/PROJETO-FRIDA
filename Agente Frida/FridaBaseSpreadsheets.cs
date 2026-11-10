// language: C#, file: FridaBaseSpreadsheets.cs, runtime: .NET Framework 4.8, target: Windows 11
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace FridaAgent
{
    public static class FridaBaseSpreadsheets
    {
        public sealed class SheetTarget
        {
            public string Label;
            public string Id;
            public string Gid;
            public string Url;
        }

        private static readonly List<SheetTarget> DefaultSheets = new List<SheetTarget>
        {
            new SheetTarget { Label = "Planilha_1", Id = "1sD7RnNjHBCt4TWWZizldaxi20WS7gjVmZKKEn8796PQ", Gid = "1047389234", Url = "https://docs.google.com/spreadsheets/d/1sD7RnNjHBCt4TWWZizldaxi20WS7gjVmZKKEn8796PQ/edit?gid=1047389234" },
            new SheetTarget { Label = "Planilha_2", Id = "1T2KTKIXBi-oLYmev2YTGswJbtc5M21szu_5jPn6FqXE", Gid = "0", Url = "https://docs.google.com/spreadsheets/d/1T2KTKIXBi-oLYmev2YTGswJbtc5M21szu_5jPn6FqXE/edit" },
            new SheetTarget { Label = "Planilha_3", Id = "1XG9NVT-xPbPYDLN-gbFkYXCiqmLK3RCvVj3n9ftHee0", Gid = "0", Url = "https://docs.google.com/spreadsheets/d/1XG9NVT-xPbPYDLN-gbFkYXCiqmLK3RCvVj3n9ftHee0/edit?gid=0" }
        };

        public static FridaToolReceipt ExecuteRequest(Dictionary<string, object> args)
        {
            string search = args != null && args.ContainsKey("search") ? Convert.ToString(args["search"]) : "";
            int preview = 15;
            if (args != null && args.ContainsKey("preview"))
            {
                int pVal;
                if (int.TryParse(Convert.ToString(args["preview"]), out pVal)) preview = pVal;
            }
            var receipt = new FridaToolReceipt("query_base_spreadsheets", "query", search, false);
            string json = Query(search, preview);
            receipt.Put("query_result", json);
            bool ok = !json.Contains("\"error\":");
            string summary = ok
                ? "Consulta realizada nas 3 planilhas oficiais do Google Drive com termo '" + search + "'."
                : "Erro ao consultar as planilhas base.";
            return receipt.Finish(ok ? FridaResultStatus.ProcessCompleted : FridaResultStatus.Failed, "base_spreadsheets", summary, json);
        }

        public static string Query(string search, int preview)
        {
            try
            {
                string scriptPath = FindScriptPath();
                if (!string.IsNullOrEmpty(scriptPath) && File.Exists(scriptPath))
                {
                    string output = RunPythonScript(scriptPath, search, preview);
                    if (!string.IsNullOrWhiteSpace(output)) return output;
                }

                return NativeFetchQuery(search, preview);
            }
            catch (Exception ex)
            {
                return "{\"error\":\"" + Escape(ex.Message) + "\",\"checked_all_three\":false}";
            }
        }

        private static string FindScriptPath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "..", "scripts", "query_sheets.py"),
                Path.Combine(baseDir, "scripts", "query_sheets.py"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "config", "skills", "frida-planilhas-base", "scripts", "query_sheets.py")
            };
            foreach (var path in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(path);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        private static string RunPythonScript(string scriptPath, string search, int preview)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = "\"" + scriptPath + "\" --preview " + Math.Max(1, preview) + (string.IsNullOrWhiteSpace(search) ? "" : " --search \"" + search.Replace("\"", "\\\"") + "\""),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15000);
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout)) return stdout;
            }
            return null;
        }

        private static string NativeFetchQuery(string search, int preview)
        {
            var results = new List<Dictionary<string, object>>();
            string needle = (search ?? "").Trim().ToLowerInvariant();

            foreach (var sheet in DefaultSheets)
            {
                var entry = new Dictionary<string, object>
                {
                    { "label", sheet.Label },
                    { "spreadsheet_id", sheet.Id },
                    { "gid", sheet.Gid }
                };

                string csvUrl = "https://docs.google.com/spreadsheets/d/" + sheet.Id + "/export?format=csv&gid=" + sheet.Gid;
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(csvUrl);
                    req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    req.Timeout = 12000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string body = reader.ReadToEnd();
                        if (body.IndexOf("accounts.google.com", StringComparison.OrdinalIgnoreCase) >= 0 || body.TrimStart().StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase))
                        {
                            entry["status"] = "auth_required";
                            entry["total_non_empty_rows"] = 0;
                        }
                        else
                        {
                            var rows = ParseCsvLines(body);
                            entry["status"] = "ok";
                            entry["total_non_empty_rows"] = rows.Count;
                            if (rows.Count > 0)
                            {
                                entry["headers"] = rows[0];
                                if (!string.IsNullOrEmpty(needle))
                                {
                                    var matches = new List<object>();
                                    for (int i = 1; i < rows.Count; i++)
                                    {
                                        string line = string.Join(" | ", rows[i]).ToLowerInvariant();
                                        if (line.Contains(needle))
                                        {
                                            matches.Add(new Dictionary<string, object> { { "row_number", i + 1 }, { "values", rows[i] } });
                                            if (matches.Count >= 40) break;
                                        }
                                    }
                                    entry["match_count"] = matches.Count;
                                    entry["matches"] = matches;
                                }
                                else
                                {
                                    int count = Math.Min(preview, Math.Max(0, rows.Count - 1));
                                    entry["preview_rows"] = rows.GetRange(1, count);
                                }
                            }
                        }
                    }
                }
                catch (WebException wex)
                {
                    var httpResp = wex.Response as HttpWebResponse;
                    if (httpResp != null && ((int)httpResp.StatusCode == 401 || (int)httpResp.StatusCode == 403))
                        entry["status"] = "auth_required";
                    else
                        entry["status"] = "error_" + wex.Status;
                    entry["total_non_empty_rows"] = 0;
                }
                catch (Exception ex)
                {
                    entry["status"] = "error_" + ex.GetType().Name;
                    entry["total_non_empty_rows"] = 0;
                }
                results.Add(entry);
            }

            var root = new Dictionary<string, object>
            {
                { "read_only", true },
                { "checked_all_three", true },
                { "search_term", search ?? "" },
                { "results", results }
            };
            return new JavaScriptSerializer().Serialize(root);
        }

        private static List<List<string>> ParseCsvLines(string text)
        {
            var list = new List<List<string>>();
            using (var reader = new StringReader(text ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var row = new List<string>();
                    var matches = Regex.Matches(line, "(?:^|,)(\"(?:[^\"]|\"\")*\"|[^,]*)");
                    foreach (Match m in matches)
                    {
                        string val = m.Groups[1].Value.Trim();
                        if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                            val = val.Substring(1, val.Length - 2).Replace("\"\"", "\"");
                        row.Add(val);
                    }
                    if (row.Count > 0) list.Add(row);
                }
            }
            return list;
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
        }
    }
}
