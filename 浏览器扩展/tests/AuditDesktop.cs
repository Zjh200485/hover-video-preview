// Load the shipped EXE by reflection; do not rebuild or replace it.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class AuditDesktop
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static object bridge;
    static Type bridgeType;
    static int port;
    static string key;
    static string session = Guid.NewGuid().ToString("D");
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static object Latest() { return bridgeType.GetProperty("Latest", Hidden).GetValue(bridge, null); }
    static byte[] Frame(Color color)
    {
        using (Bitmap bitmap = new Bitmap(64, 36))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (MemoryStream memory = new MemoryStream())
        { graphics.Clear(color); bitmap.Save(memory, ImageFormat.Jpeg); return memory.ToArray(); }
    }
    static TcpClient Partial(byte[] bytes, int sequence)
    {
        TcpClient client = new TcpClient(); client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 4000;
        string header = "POST /frame HTTP/1.1\r\nHost: 127.0.0.1\r\nX-Hover-Key: " + key
            + "\r\nX-Hover-Session: " + session + "\r\nX-Hover-Sequence: " + sequence + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
        byte[] head = Encoding.ASCII.GetBytes(header);
        NetworkStream stream = client.GetStream(); stream.Write(head, 0, head.Length);
        stream.Write(bytes, 0, bytes.Length - 1); stream.Flush();
        Thread.Sleep(100); return client;
    }
    static void Finish(TcpClient client, byte[] bytes, int expected)
    {
        NetworkStream stream = client.GetStream(); stream.Write(bytes, bytes.Length - 1, 1);
        using (StreamReader reader = new StreamReader(stream))
            Check(reader.ReadToEnd().StartsWith("HTTP/1.1 " + expected), "Unexpected frame response");
        client.Close();
    }
    static void Command(string command)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + command);
        request.Proxy = null; request.Method = "POST"; request.ContentLength = 0;
        request.Timeout = 4000; request.Headers["X-Hover-Key"] = key;
        request.Headers["X-Hover-Session"] = session;
        using (WebResponse response = request.GetResponse()) { }
    }
    static Color Pixel()
    {
        object frame = Latest(); byte[] bytes = (byte[])frame.GetType().GetField("Bytes", Hidden).GetValue(frame);
        using (MemoryStream memory = new MemoryStream(bytes))
        using (Bitmap image = new Bitmap(memory)) return image.GetPixel(30, 20);
    }
    public static int Main(string[] args)
    {
        try
        {
            Assembly assembly = Assembly.LoadFrom(args[0]);
            bridgeType = assembly.GetType("HoverVideoPreview.BrowserBridge", true);
            bridge = Activator.CreateInstance(bridgeType, Hidden, null, new object[] { 0 }, null);
            string code = (string)bridgeType.GetProperty("ConnectionCode", Hidden).GetValue(bridge, null);
            string[] parts = code.Split(':'); port = Int32.Parse(parts[0]); key = parts[1];
            byte[] oldFrame = Frame(Color.Blue), newFrame = Frame(Color.Red);
            Command("/start");
            using (TcpClient pending = Partial(oldFrame, 1))
            {
                Command("/stop"); Check(Latest() == null, "Stop did not clear frame");
                Finish(pending, oldFrame, 409);
                Check(Latest() == null, "Late frame resurrected a stopped session");
                Console.WriteLine("PASS: in-flight frame rejected after /stop; preview remains cleared.");
            }
            session = Guid.NewGuid().ToString("D");Command("/start");
            using (TcpClient pending = Partial(oldFrame, 1))
            {
                using (TcpClient newer = Partial(newFrame, 2)) Finish(newer, newFrame, 200);
                Check(Pixel().R > 200, "New frame missing");
                Finish(pending, oldFrame, 409);
                Check(Pixel().R > 200, "Old frame overwrote the newer frame");
                Console.WriteLine("PASS: delayed older sequence rejected; newest frame preserved.");
            }
            using (TcpClient pending = Partial(oldFrame, 3))
            {
                string oldSession = session;
                string newSession = Guid.NewGuid().ToString("D");
                session = newSession;Command("/start");
                Finish(pending, oldFrame, 409);
                Check(Latest() == null, "Old session frame crossed the reconnect boundary");
                session = oldSession;Command("/stop");
                session = newSession;
                using (TcpClient newer = Partial(newFrame, 1)) Finish(newer, newFrame, 200);
                Check(Pixel().R > 200, "Old stop command stopped the new session");
                Console.WriteLine("PASS: old-session frame rejected after a new capture begins.");
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (bridge != null) ((IDisposable)bridge).Dispose(); }
    }
}
