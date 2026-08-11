using System;
using System.Collections;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MCPForUnity.Editor.Services.Transport.Transports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MCPForUnityTests.Editor.Services
{
    /// <summary>
    /// Tests that StdioBridgeHost correctly handles client reconnection scenarios.
    /// After an abrupt client disconnect, a new client must be able to connect and
    /// have its commands processed — this was broken by the zombie state bug (#785).
    /// </summary>
    [TestFixture]
    public class StdioBridgeReconnectTests
    {
        private const int ConnectTimeoutMs = 5000;
        private const int ReadTimeoutMs = 10000;

        [UnityTest]
        public IEnumerator NewClient_AfterAbruptDisconnect_CanSendAndReceiveCommands()
        {
            if (!StdioBridgeHost.IsRunning)
            {
                Assert.Ignore("StdioBridgeHost is not running; skipping reconnect test.");
                yield break;
            }

            int port = StdioBridgeHost.GetCurrentPort();

            // --- First client: connect, verify ping/pong, then abruptly close ---
            using (var client1 = new TcpClient())
            {
                Assert.IsTrue(client1.ConnectAsync("127.0.0.1", port).Wait(ConnectTimeoutMs),
                    "First client connect timed out");
                client1.ReceiveTimeout = ReadTimeoutMs;
                var stream1 = client1.GetStream();

                string handshake1 = ReadLine(stream1, ReadTimeoutMs);
                Assert.That(handshake1, Does.Contain("FRAMING=1"), "First client should receive handshake");
                CompleteHandshake(stream1, handshake1);

                // Send a framed ping
                SendFrame(stream1, Encoding.UTF8.GetBytes("ping"));
                byte[] pongBytes = ReadFrame(stream1, ReadTimeoutMs);
                string pong1 = Encoding.UTF8.GetString(pongBytes);
                Assert.That(pong1, Does.Contain("pong"), "First client should get pong response");

                // Abrupt close — simulates server crash / domain reload disconnect
                client1.Client.LingerState = new LingerOption(true, 0);
                client1.Close();
            }

            // Wait a few frames for cleanup
            for (int i = 0; i < 10; i++)
                yield return null;

            // --- Second client: connect and verify commands still work ---
            using (var client2 = new TcpClient())
            {
                Assert.IsTrue(client2.ConnectAsync("127.0.0.1", port).Wait(ConnectTimeoutMs),
                    "Second client connect timed out");
                client2.ReceiveTimeout = ReadTimeoutMs;
                var stream2 = client2.GetStream();

                string handshake2 = ReadLine(stream2, ReadTimeoutMs);
                Assert.That(handshake2, Does.Contain("FRAMING=1"), "Second client should receive handshake");
                CompleteHandshake(stream2, handshake2);

                // Send a framed ping — this is the critical check that would fail
                // if the bridge is in zombie state.
                SendFrame(stream2, Encoding.UTF8.GetBytes("ping"));
                byte[] pongBytes2 = ReadFrame(stream2, ReadTimeoutMs);
                string pong2 = Encoding.UTF8.GetString(pongBytes2);
                Assert.That(pong2, Does.Contain("pong"), "Second client should get pong response after reconnect");

                client2.Close();
            }
        }

        [UnityTest]
        public IEnumerator ConcurrentClients_AreServedIndependently()
        {
            if (!StdioBridgeHost.IsRunning)
            {
                Assert.Ignore("StdioBridgeHost is not running; skipping reconnect test.");
                yield break;
            }

            int port = StdioBridgeHost.GetCurrentPort();

            var client1 = new TcpClient();
            var client2 = new TcpClient();
            try
            {
                Assert.IsTrue(client1.ConnectAsync("127.0.0.1", port).Wait(ConnectTimeoutMs),
                    "First client connect timed out");
                client1.ReceiveTimeout = ReadTimeoutMs;
                var stream1 = client1.GetStream();
                string handshake1 = ReadLine(stream1, ReadTimeoutMs);
                Assert.That(handshake1, Does.Contain("FRAMING=1"),
                    "First client should receive handshake");
                CompleteHandshake(stream1, handshake1);

                SendFrame(stream1, Encoding.UTF8.GetBytes("ping"));
                Assert.That(Encoding.UTF8.GetString(ReadFrame(stream1, ReadTimeoutMs)), Does.Contain("pong"));

                Assert.IsTrue(client2.ConnectAsync("127.0.0.1", port).Wait(ConnectTimeoutMs),
                    "Second client connect timed out");
                client2.ReceiveTimeout = ReadTimeoutMs;
                var stream2 = client2.GetStream();
                string handshake2 = ReadLine(stream2, ReadTimeoutMs);
                Assert.That(handshake2, Does.Contain("FRAMING=1"),
                    "Second client should receive handshake");
                CompleteHandshake(stream2, handshake2);

                SendFrame(stream2, Encoding.UTF8.GetBytes("ping"));
                Assert.That(Encoding.UTF8.GetString(ReadFrame(stream2, ReadTimeoutMs)), Does.Contain("pong"),
                    "Second client should be served while the first is still connected");

                // The first client must still be alive — a second agent connecting
                // must not stomp existing connections mid-session.
                SendFrame(stream1, Encoding.UTF8.GetBytes("ping"));
                Assert.That(Encoding.UTF8.GetString(ReadFrame(stream1, ReadTimeoutMs)), Does.Contain("pong"),
                    "First client must remain connected after a second client joins");
            }
            finally
            {
                try { client1.Close(); } catch { }
                try { client2.Close(); } catch { }
            }
        }

        [Test]
        public void HandshakeBanner_IdentifiesProjectAndPort()
        {
            string banner = StdioBridgeHost.BuildHandshakeBanner();

            StringAssert.StartsWith("WELCOME UNITY-MCP 2 FRAMING=1 ", banner);
            Assert.That(banner, Does.Contain(" BRIDGE_PROTOCOL=2 "));
            Assert.That(banner, Does.Match(@" UNITY_PACKAGE=\S+"));
            Assert.That(banner, Does.Match(@" RESOURCES=\S+"));
            Assert.That(banner, Does.Match(@" TOOL_GROUPS=\S+"));
            Assert.That(banner, Does.Match(@" PROJECT=[0-9a-f]{8}\b"),
                "banner must carry the project hash so clients can detect a wrong/stale port");
            Assert.That(banner, Does.Match(@" PORT=\d+"));
            Assert.IsTrue(banner.EndsWith("\n"), "banner must be newline-terminated");
        }

        #region Frame protocol helpers

        private static void CompleteHandshake(NetworkStream stream, string banner)
        {
            string packageVersion = "unknown";
            foreach (string token in banner.Split(' '))
            {
                if (token.StartsWith("UNITY_PACKAGE=", StringComparison.Ordinal))
                {
                    packageVersion = token.Substring("UNITY_PACKAGE=".Length);
                    break;
                }
            }

            var request = new JObject
            {
                ["type"] = "bridge_handshake",
                ["params"] = new JObject
                {
                    ["handshake"] = new JObject
                    {
                        ["bridge_protocol_version"] = 2,
                        ["python_server"] = new JObject
                        {
                            ["version"] = packageVersion,
                            ["git_sha"] = "test-sha",
                            ["registered_resources"] = new JArray(
                                "mcpforunity://capabilities",
                                "mcpforunity://workflow"),
                            ["tool_groups"] = new JArray("core")
                        }
                    }
                }
            };
            SendFrame(stream, Encoding.UTF8.GetBytes(request.ToString(Formatting.None)));
            JObject response = JObject.Parse(Encoding.UTF8.GetString(
                ReadFrame(stream, ReadTimeoutMs)));
            Assert.That(response.Value<string>("status"), Is.EqualTo("success"),
                response.Value<string>("error"));
        }

        private static string ReadLine(NetworkStream stream, int timeoutMs)
        {
            var sb = new StringBuilder();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            stream.ReadTimeout = timeoutMs;

            while (DateTime.UtcNow < deadline)
            {
                int b = stream.ReadByte();
                if (b < 0)
                    throw new IOException("Connection closed while reading line");
                if (b == '\n')
                    return sb.ToString();
                sb.Append((char)b);
            }
            throw new TimeoutException("Timed out reading line from stream");
        }

        private static void SendFrame(NetworkStream stream, byte[] payload)
        {
            byte[] header = new byte[8];
            ulong len = (ulong)payload.LongLength;
            header[0] = (byte)(len >> 56);
            header[1] = (byte)(len >> 48);
            header[2] = (byte)(len >> 40);
            header[3] = (byte)(len >> 32);
            header[4] = (byte)(len >> 24);
            header[5] = (byte)(len >> 16);
            header[6] = (byte)(len >> 8);
            header[7] = (byte)(len);
            stream.Write(header, 0, 8);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static byte[] ReadFrame(NetworkStream stream, int timeoutMs)
        {
            stream.ReadTimeout = timeoutMs;

            byte[] header = ReadExact(stream, 8, timeoutMs);
            ulong payloadLen =
                ((ulong)header[0] << 56) | ((ulong)header[1] << 48) |
                ((ulong)header[2] << 40) | ((ulong)header[3] << 32) |
                ((ulong)header[4] << 24) | ((ulong)header[5] << 16) |
                ((ulong)header[6] << 8)  | header[7];

            if (payloadLen == 0 || payloadLen > 16 * 1024 * 1024)
                throw new IOException($"Invalid frame length: {payloadLen}");

            return ReadExact(stream, (int)payloadLen, timeoutMs);
        }

        private static byte[] ReadExact(NetworkStream stream, int count, int timeoutMs)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (offset < count)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Timed out reading {count} bytes (got {offset})");

                int remaining = count - offset;
                int read = stream.Read(buffer, offset, remaining);
                if (read == 0)
                    throw new IOException("Connection closed before reading expected bytes");
                offset += read;
            }

            return buffer;
        }

        #endregion
    }
}
