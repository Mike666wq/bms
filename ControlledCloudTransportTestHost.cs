using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BmsSerialDemo
{
    // A deliberately tiny HTTP/1.1 server bound only to IPv4 loopback for transport tests.
    internal sealed class ControlledCloudTransportTestHost : IDisposable
    {
        internal sealed class Reply
        {
            public int Status = 200;
            public string Body = "{\"subscriptionId\":\"test-sub\",\"leaseSeconds\":30,\"requestedPacks\":[1]}";
            public bool Chunked;
            public int DelayBeforeHeaders;
            public int DelayBeforeBody;
            public string Redirect;
        }
        readonly TcpListener listener;
        readonly Thread thread;
        volatile bool stopping;
        volatile Reply next = new Reply();
        internal int Port { get; private set; }
        internal string Endpoint { get { return "http://127.0.0.1:" + Port + "/"; } }
        internal string LastAuthorization { get; private set; }
        internal string LastPath { get; private set; }
        internal string LastBody { get; private set; }
        int requests;
        internal int Requests { get { return Thread.VolatileRead(ref requests); } }
        internal ControlledCloudTransportTestHost()
        {
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            thread = new Thread(Serve); thread.IsBackground = true; thread.Start();
        }
        internal void SetReply(Reply reply) { next = reply ?? new Reply(); }
        void Serve()
        {
            while (!stopping)
            {
                TcpClient client = null;
                try { client = listener.AcceptTcpClient(); }
                catch { if (stopping) return; Thread.Sleep(100); continue; }
                using (client) try { Handle(client); } catch { }
            }
        }
        void Handle(TcpClient client)
        {
            NetworkStream stream = client.GetStream(); stream.ReadTimeout = 5000; stream.WriteTimeout = 5000;
            string requestLine = ReadLine(stream); if (requestLine == null) return;
            LastPath = requestLine.Split(' ').Length > 1 ? requestLine.Split(' ')[1] : "";
            int length = 0; string authorization = "";
            string line; while (!String.IsNullOrEmpty(line = ReadLine(stream)))
            { int colon = line.IndexOf(':'); if (colon < 0) continue; string name = line.Substring(0, colon).Trim(); string value = line.Substring(colon + 1).Trim(); if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) Int32.TryParse(value, out length); if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = value; }
            LastAuthorization = authorization;
            byte[] drain = new byte[4096]; using (MemoryStream requestBody = new MemoryStream()) { while (length > 0) { int n = stream.Read(drain, 0, Math.Min(drain.Length, length)); if (n <= 0) break; requestBody.Write(drain, 0, n); length -= n; } LastBody = Encoding.UTF8.GetString(requestBody.ToArray()); }
            Reply reply = next;
            Interlocked.Increment(ref requests);
            if (reply.DelayBeforeHeaders > 0) Thread.Sleep(reply.DelayBeforeHeaders);
            byte[] body = Encoding.UTF8.GetBytes(reply.Body ?? "");
            StringBuilder headers = new StringBuilder("HTTP/1.1 ").Append(reply.Status).Append(reply.Status == 200 ? " OK\r\n" : " Test\r\n").Append("Connection: close\r\n");
            if (!String.IsNullOrEmpty(reply.Redirect)) headers.Append("Location: ").Append(reply.Redirect).Append("\r\n");
            if (reply.Chunked) headers.Append("Transfer-Encoding: chunked\r\n"); else headers.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            headers.Append("Content-Type: application/json\r\n\r\n"); byte[] head = Encoding.ASCII.GetBytes(headers.ToString()); stream.Write(head, 0, head.Length); stream.Flush();
            if (reply.DelayBeforeBody > 0) Thread.Sleep(reply.DelayBeforeBody);
            if (reply.Chunked)
            {
                int at = 0; while (at < body.Length && client.Connected) { int n = Math.Min(4096, body.Length - at); byte[] chunkHead = Encoding.ASCII.GetBytes(n.ToString("X") + "\r\n"); stream.Write(chunkHead, 0, chunkHead.Length); stream.Write(body, at, n); stream.Write(new byte[] { 13, 10 }, 0, 2); at += n; }
                byte[] end = Encoding.ASCII.GetBytes("0\r\n\r\n"); stream.Write(end, 0, end.Length);
            }
            else stream.Write(body, 0, body.Length);
            stream.Flush();
        }
        static string ReadLine(Stream stream)
        { MemoryStream ms = new MemoryStream(); int previous = -1; while (ms.Length < 8192) { int b = stream.ReadByte(); if (b < 0) return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray()); if (previous == 13 && b == 10) { byte[] bytes = ms.ToArray(); return Encoding.ASCII.GetString(bytes, 0, Math.Max(0, bytes.Length - 1)); } ms.WriteByte((byte)b); previous = b; } throw new IOException("HTTP header line too long"); }
        public void Dispose() { stopping = true; try { listener.Stop(); } catch { } try { if (thread != null && thread.IsAlive) thread.Join(1000); } catch { } }
    }
}
