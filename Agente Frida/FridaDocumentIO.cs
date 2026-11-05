using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FridaAgent
{
    public sealed class FridaTextRead
    {
        public string Path {get;internal set;}
        public string Text {get;internal set;}
        public int Offset {get;internal set;}
        public bool HasMore {get;internal set;}
        public string Describe()
        {
            return "Leitura observada: "+Path+"; offset="+Offset+"; caracteres="+Text.Length+". "+(HasMore?"PARCIAL; continue com offset="+(Offset+Text.Length):"Fim do arquivo.")+"\n"+Text;
        }
    }
    public static class FridaDocumentIO
    {
        private static void Entry(ZipArchive zip, string name, string text)
        { using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(text); }
        public static void WriteDocx(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    Entry(zip,"[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
                    Entry(zip,"_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                    var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), CloseOutput = true };
                    using (var writer = XmlWriter.Create(zip.CreateEntry("word/document.xml").Open(), settings))
                    {
                        const string ns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                        writer.WriteStartDocument(); writer.WriteStartElement("w","document",ns); writer.WriteStartElement("w","body",ns);
                        foreach (string line in (text ?? "").Replace("\r\n","\n").Replace('\r','\n').Split('\n'))
                        {
                            writer.WriteStartElement("w","p",ns); writer.WriteStartElement("w","r",ns); writer.WriteStartElement("w","t",ns);
                            writer.WriteAttributeString("xml","space",null,"preserve"); writer.WriteString(line);
                            writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndElement();
                        }
                        writer.WriteEndElement();writer.WriteEndElement();writer.WriteEndDocument();
                    }
                }
                // Validate before replacing any existing document.
                ReadDocx(temporary, 0, 1);
                if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static string ReadDocx(string path, int offset, int limit)
        {
            using (var file = File.OpenRead(path))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Read))
            {
                var types=zip.GetEntry("[Content_Types].xml");var rels=zip.GetEntry("_rels/.rels");
                if(types==null || rels==null || types.Length>1024*1024 || rels.Length>1024*1024)throw new IOException("Pacote DOCX sem as partes de identificacao necessarias.");
                bool documentType=false,documentRelation=false;
                using(var reader=XmlReader.Create(types.Open(),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
                    while(reader.Read())if(reader.NodeType==XmlNodeType.Element && reader.LocalName=="Override" && reader.GetAttribute("PartName")=="/word/document.xml" && reader.GetAttribute("ContentType")=="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")documentType=true;
                using(var reader=XmlReader.Create(rels.Open(),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
                    while(reader.Read())if(reader.NodeType==XmlNodeType.Element && reader.LocalName=="Relationship" && reader.GetAttribute("Type")=="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" && (reader.GetAttribute("Target") ?? "").TrimStart('/')=="word/document.xml")documentRelation=true;
                if(!documentType || !documentRelation)throw new IOException("Estrutura DOCX nao reconhecida por este leitor.");
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null || entry.Length > 16*1024*1024) throw new IOException("Documento ausente, invalido ou grande demais para esta leitura.");
                var text = new StringBuilder();
                using (var reader = XmlReader.Create(entry.Open(),new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    while (reader.Read())
                    {
                        if (reader.NodeType == XmlNodeType.Text) text.Append(reader.Value);
                        if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") text.AppendLine();
                    }
                string result=text.ToString(); if(offset>=result.Length)return "";
                return result.Substring(offset,Math.Min(limit,result.Length-offset));
            }
        }
        public static string ReadText(string path, int offset, int limit)
        {return ReadSnapshot(path,offset,limit).Describe();}
        public static FridaTextRead ReadSnapshot(string path, int offset, int limit)
        {
            if (offset < 0 || offset > 32*1024*1024) throw new ArgumentOutOfRangeException("offset");
            limit = Math.Max(1,Math.Min(16000,limit));
            string extension=Path.GetExtension(path).ToLowerInvariant();
            string value;
            if(extension==".docx") value=ReadDocx(path,offset,limit+1);
            else
            {
                if(System.Text.RegularExpressions.Regex.IsMatch(extension,@"^\.(pdf|doc|xlsx|xls|pptx|ppt|exe|dll|zip|png|jpg|jpeg|gif|wav|mp3|dwg|sldprt|sldasm)$"))
                    throw new IOException("Formato binario exige extracao ou ferramenta apropriada; nao pode ser interpretado como texto: "+extension);
                using(var reader=new StreamReader(path,Encoding.UTF8,true))
                {
                    char[] buffer=new char[4096];int skipped=0,count;
                    while(skipped<offset && (count=reader.Read(buffer,0,Math.Min(buffer.Length,offset-skipped)))>0)skipped+=count;
                    buffer=new char[limit+1];count=0;int n;
                    while(count<buffer.Length && (n=reader.Read(buffer,count,buffer.Length-count))>0)count+=n;
                    value=new string(buffer,0,count);
                    if(value.IndexOf('\0')>=0)throw new IOException("Arquivo binario detectado; use extracao apropriada.");
                }
            }
            bool more=value.Length>limit;if(more)value=value.Substring(0,limit);
            return new FridaTextRead {Path=path,Offset=offset,Text=value,HasMore=more};
        }
    }
}
