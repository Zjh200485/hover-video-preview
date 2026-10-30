using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HoverVideoPreview
{
    internal sealed class BrowserFrame
    {
        internal readonly byte[] Bytes;
        internal readonly long Sequence;
        internal readonly DateTime Received;
        internal BrowserFrame(byte[] bytes, long sequence) { Bytes = bytes; Sequence = sequence; Received = DateTime.UtcNow; }
    }

    // Only the loopback interface is used; frames live in RAM, never in a recording file.
    internal sealed class BrowserBridge : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread worker;
        private readonly object sync = new object();
        private readonly string key;
        private BrowserFrame latest;
        private long sequence;
        private string captureSession;
        private long senderSequence;
        private volatile bool stopping;
        internal readonly int Port;
        internal string ConnectionCode { get { return Port + ":" + key; } }
        internal BrowserFrame Latest { get { lock (sync) return latest; } }
        internal BrowserBridge(int port)
        {
            byte[] random = new byte[24]; using (RandomNumberGenerator generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            key = Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            listener = new TcpListener(IPAddress.Loopback, port); listener.Start(8);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            worker = new Thread(AcceptLoop); worker.IsBackground = true; worker.Start();
        }
        private void AcceptLoop()
        {
            while (!stopping)
            {
                try { TcpClient client = listener.AcceptTcpClient(); ThreadPool.QueueUserWorkItem(delegate { Serve(client); }); }
                catch (SocketException) { if (stopping) return; }
                catch (ObjectDisposedException) { return; }
            }
        }
        private static string ReadLine(NetworkStream stream)
        {
            List<byte> bytes = new List<byte>();
            while (bytes.Count <= 4096)
            {
                int value = stream.ReadByte(); if (value < 0) throw new IOException("连接提前关闭。");
                if (value == 10) return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add((byte)value);
            }
            throw new IOException("请求头过长。");
        }
        private void Serve(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = client.SendTimeout = 4000;
                try
                {
                    using (NetworkStream stream = client.GetStream())
                    {
                        string[] request = ReadLine(stream).Split(' ');
                        if (request.Length != 3) return;
                        Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int count = 0; count < 40; count++)
                        {
                            string line = ReadLine(stream); if (line.Length == 0) break;
                            int colon = line.IndexOf(':'); if (colon > 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                            if (count == 39) throw new IOException("请求头过多。");
                        }
                        string origin; headers.TryGetValue("Origin", out origin);
                        if (request[0] == "OPTIONS") { Respond(stream, 204, "", origin); return; }
                        string suppliedKey;
                        if (!headers.TryGetValue("X-Hover-Key", out suppliedKey) || suppliedKey != key)
                        { Respond(stream, 403, "{\"error\":\"连接码不匹配\"}", origin); return; }
                        if (request[0] == "GET" && request[1] == "/status")
                        {
                            BrowserFrame frame = Latest;
                            Respond(stream, 200, "{\"ok\":true,\"version\":3,\"frames\":" + Interlocked.Read(ref sequence) + ",\"recent\":" + (frame != null && (DateTime.UtcNow - frame.Received).TotalSeconds < 3 ? "true" : "false") + "}", origin); return;
                        }
                        string session; Guid parsedSession;
                        if (!headers.TryGetValue("X-Hover-Session", out session) || !Guid.TryParseExact(session, "D", out parsedSession))
                        { Respond(stream, 400, "{}", origin); return; }
                        if (request[0] == "POST" && request[1] == "/start")
                        { lock (sync) { captureSession = session; senderSequence = 0; latest = null; } Respond(stream, 200, "{\"ok\":true}", origin); return; }
                        if (request[0] == "POST" && request[1] == "/stop")
                        { lock (sync) { if (captureSession == session) { captureSession = null; latest = null; } } Respond(stream, 200, "{\"ok\":true}", origin); return; }
                        if (request[0] != "POST" || request[1] != "/frame") { Respond(stream, 404, "{}", origin); return; }
                        string frameText; long frameSequence;
                        if (!headers.TryGetValue("X-Hover-Sequence", out frameText) || !Int64.TryParse(frameText, out frameSequence) || frameSequence < 1)
                        { Respond(stream, 400, "{}", origin); return; }
                        lock (sync) if (captureSession != session || frameSequence <= senderSequence)
                        { Respond(stream, 409, "{}", origin); return; }
                        string lengthText; int length;
                        if (!headers.TryGetValue("Content-Length", out lengthText) || !Int32.TryParse(lengthText, out length) || length < 4 || length > 2500000)
                        { Respond(stream, 413, "{}", origin); return; }
                        byte[] bytes = new byte[length]; int offset = 0;
                        while (offset < length) { int read = stream.Read(bytes, offset, length - offset); if (read == 0) throw new IOException(); offset += read; }
                        if (bytes[0] != 255 || bytes[1] != 216) { Respond(stream, 415, "{}", origin); return; }
                        // Validate dimensions before admitting a frame to the UI.
                        using (MemoryStream buffer = new MemoryStream(bytes))
                        using (Image image = Image.FromStream(buffer))
                            if (image.Width < 2 || image.Height < 2 || image.Width > 2048 || image.Height > 2048) { Respond(stream, 413, "{}", origin); return; }
                        // Admission is rechecked after the body and image have
                        // been read: stop/new-session may have happened meanwhile.
                        lock (sync)
                        {
                            if (stopping || captureSession != session || frameSequence <= senderSequence)
                            { Respond(stream, 409, "{}", origin); return; }
                            senderSequence = frameSequence;
                            latest = new BrowserFrame(bytes, Interlocked.Increment(ref sequence));
                        }
                        Respond(stream, 200, "{\"ok\":true}", origin);
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ArgumentException) { }
                catch (OutOfMemoryException) { }
            }
        }
        private static void Respond(NetworkStream stream, int code, string body, string origin)
        {
            byte[] data = Encoding.UTF8.GetBytes(body);
            string cors = "";
            if (!String.IsNullOrEmpty(origin) && origin.StartsWith("chrome-extension://", StringComparison.Ordinal) && origin.IndexOf('\r') < 0 && origin.IndexOf('\n') < 0)
                cors = "Access-Control-Allow-Origin: " + origin + "\r\nVary: Origin\r\n";
            string header = "HTTP/1.1 " + code + " " + (code < 300 ? "OK" : "Error") + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + data.Length
                + "\r\nConnection: close\r\nCache-Control: no-store\r\n" + cors
                + "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, X-Hover-Key, X-Hover-Session, X-Hover-Sequence\r\nAccess-Control-Allow-Private-Network: true\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(header); stream.Write(head, 0, head.Length); stream.Write(data, 0, data.Length);
        }
        public void Dispose()
        { stopping = true; listener.Stop(); worker.Join(1000); lock (sync) latest = null; }
    }
}
