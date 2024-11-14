using System;
using System.Collections.Generic;
using Phoenix;
using NUnit.Framework;
using System.Net;
using Newtonsoft.Json.Linq;
using static Phoenix.Presence;

namespace PhoenixTests
{
    public sealed class BasicLogger : ILogger
    {
        #region ILogger implementation

        public void Log(Phoenix.LogLevel level, string source, string message)
        {
            Console.WriteLine("[{0}]: {1} - {2}", level, source, message);
        }

        #endregion
    }


    [TestFixture]
    public class IntegrationTests
    {
        private const int networkDelay = 500 /* ms */;
        private const string host = "localhost:4000";

        [Test]
        public void GeneralIntegrationTest()
        {
            /// 
            /// setup
            /// 
            var address = string.Format("http://{0}/ops/heartbeat", host);

            // heroku health check
            using (var client = new WebClient())
            {
                // client.Headers.Add("Content-Type", "application/json");
                client.DownloadString(address);
            }

            var onOpenCount = 0;
            Socket.OnOpenDelegate onOpenCallback = () => onOpenCount++;

            var onMessageData = new List<string>();
            Socket.OnMessageDelegate onMessageCallback = m => onMessageData.Add(m);

            // connecting is synchronous as implemented above
            var socketFactory = new WebsocketSharpFactory();
            var socketOptions = new Socket.Options
            {
                channelRejoinInterval = TimeSpan.FromMilliseconds(200),
                heartbeatInterval = TimeSpan.FromMilliseconds(100),
                logger = new BasicLogger()
            };
            var socket = new Socket(socketFactory, socketOptions);

            socket.OnOpen += onOpenCallback;
            socket.OnMessage += onMessageCallback;

            socket.Connect(string.Format("ws://{0}/phoenix_sharp_test", host), null);
            Assert.IsTrue(socket.state == Socket.State.Open);
            Assert.AreEqual(1, onOpenCount);

            /// 
            /// test channel error on join
            /// 
            Reply? okReply = null;
            Reply? errorReply = null;
            bool closeCalled = false;

            var errorChannel = socket.MakeChannel("tester:phoenix-sharp");
            errorChannel.On(Message.InBoundEvent.phx_close, _ => closeCalled = true);

            errorChannel.Join()
                .Receive(Reply.Status.Ok, r => okReply = r)
                .Receive(Reply.Status.Error, r => errorReply = r);

            Assert.That(() => errorReply.HasValue, Is.True.After(networkDelay, 10));
            Assert.IsNull(okReply);
            Assert.AreEqual(Channel.State.Errored, errorChannel.state);
            // call leave explicitly to cleanup and avoid rejoin attempts
            errorChannel.Leave();
            Assert.IsTrue(closeCalled);

            /// 
            /// test channel joining and receiving a custom event
            /// 
            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;

            Message afterJoinMessage = null;
            Message closeMessage = null;
            Message errorMessage = null;

            var param = new Dictionary<string, object> {
                { "auth", "doesn't matter" },
            };

            var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
            roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
            roomChannel.On(Message.InBoundEvent.phx_error, m => errorMessage = m);
            roomChannel.On("after_join", m => afterJoinMessage = m);

            roomChannel.Join(param)
                .Receive(Reply.Status.Ok, r => joinOkReply = r)
                .Receive(Reply.Status.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply.HasValue, Is.True.After(networkDelay, 10));
            Assert.IsNull(joinErrorReply);

            Assert.That(() => afterJoinMessage != null, Is.True.After(networkDelay, 10));
            Assert.AreEqual("Welcome!", afterJoinMessage.payload["message"].Value<string>());

            // 1. error, 2. join, 3. after_join
            Assert.AreEqual(3, onMessageData.Count, "Unexpected message count: " + string.Join("; ", onMessageData));

            /// 
            /// test echo reply
            /// 
            var payload = new Dictionary<string, object> {
                    { "echo", "test" }
            };

            Reply? testOkReply = null;

            roomChannel
                .Push("reply_test", payload)
                .Receive(Reply.Status.Ok, r => testOkReply = r);

            Assert.That(() => testOkReply.HasValue, Is.True.After(networkDelay, 10));
            Assert.IsNotNull(testOkReply.Value.response);
            CollectionAssert.AreEquivalent(testOkReply.Value.response.ToObject<Dictionary<string, object>>(), payload);

            /// 
            /// test error reply
            /// 
            Reply? testErrorReply = null;

            roomChannel
                .Push("error_test")
                .Receive(Reply.Status.Error, r => testErrorReply = r);

            Assert.That(() => testErrorReply.HasValue, Is.True.After(networkDelay, 10));
            Assert.AreEqual(testErrorReply.Value.status, Reply.Status.Error);

            /// 
            /// test timeout reply
            /// 
            Reply? testTimeoutReply = null;

            roomChannel
                .Push("timeout_test", null, TimeSpan.FromMilliseconds(50))
                .Receive(Reply.Status.Timeout, r => testTimeoutReply = r);

            Assert.That(() => testTimeoutReply.HasValue, Is.False.After(40));
            Assert.That(() => testTimeoutReply.HasValue, Is.True.After(60));

            ///	
            /// test channel error/rejoin
            /// 
            Assert.IsNull(errorMessage);
            // we track rejoining through the same join push callback we setup
            joinOkReply = null;

            socket.Disconnect();
            socket.Connect(string.Format("ws://{0}/phoenix_sharp_test", host), null);

            Assert.That(() => errorMessage != null, Is.True.After(networkDelay, 10));
            Assert.That(() => joinOkReply != null, Is.True.After(networkDelay, 10));
            Assert.That(() => roomChannel.CanPush, Is.True.After(networkDelay, 10));

            /// 
            /// test duplicate channel join
            /// 
            joinOkReply = null;
            joinErrorReply = null;
            errorMessage = null;
            Assert.IsNull(closeMessage);
            Message newCloseMessage = null;

            var newRoomChannel = socket.MakeChannel("tester:phoenix-sharp");
            newRoomChannel.On(Message.InBoundEvent.phx_close, m => newCloseMessage = m);

            newRoomChannel.Join(param)
                .Receive(Reply.Status.Ok, r => joinOkReply = r)
                .Receive(Reply.Status.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply.HasValue, Is.True.After(networkDelay, 10));
            Assert.IsNull(joinErrorReply);
            Assert.IsNotNull(errorMessage);
            Assert.IsNotNull(closeMessage);
            Assert.That(() => newCloseMessage, Is.Not.Null.After(networkDelay, 50));

            /// 
            /// test channel leave
            /// also, it should discard any additional messages
            /// 
            roomChannel = socket.MakeChannel("tester:phoenix-sharp");
            roomChannel.Join(param)
                .Receive(Reply.Status.Ok, r => joinOkReply = r)
                .Receive(Reply.Status.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply.HasValue, Is.True.After(networkDelay, 10));
            Message pushMessage = null;

            roomChannel.On("push_test", m => pushMessage = m);
            roomChannel.Push("push_test", payload);

            Assert.IsNull(pushMessage);
            roomChannel.Leave();

            Assert.That(() => closeMessage != null, Is.True.After(networkDelay, 10));
            Assert.IsNull(pushMessage); // ignored
        }

        [Test]
        public void MultipleJoinIntegrationTest()
        {
            var onOpenCount = 0;
            Socket.OnOpenDelegate onOpenCallback = () => onOpenCount++;
            Socket.OnCloseDelegate onCloseCallback = (code, message) => onOpenCount--;

            var onMessageData = new List<string>();
            Socket.OnMessageDelegate onMessageCallback = m => onMessageData.Add(m);

            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(socketFactory, new Socket.Options
            {
                channelRejoinInterval = TimeSpan.FromMilliseconds(200),
                logger = new BasicLogger()
            });

            socket.OnOpen += onOpenCallback;
            socket.OnClose += onCloseCallback;
            socket.OnMessage += onMessageCallback;

            socket.Connect(string.Format("ws://{0}/phoenix_sharp_test", host), null);
            Assert.IsTrue(socket.state == Socket.State.Open);
            Assert.AreEqual(1, onOpenCount);

            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;
            Message afterJoinMessage = null;
            Message closeMessage = null;
            Message errorMessage = null;

            //Try to join for the first time
            var param = new Dictionary<string, object> {
                { "auth", "doesn't matter" },
            };

            var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
            roomChannel.On(Message.InBoundEvent.phx_close, m => closeMessage = m);
            roomChannel.On(Message.InBoundEvent.phx_error, m => errorMessage = m);
            roomChannel.On("after_join", m => afterJoinMessage = m);

            roomChannel.Join(param)
                .Receive(Reply.Status.Ok, r => joinOkReply = r)
                .Receive(Reply.Status.Error, r => joinErrorReply = r);

            Assert.That(() => joinOkReply.HasValue, Is.True.After(networkDelay, 10));
            Assert.IsNull(joinErrorReply);

            Assert.That(() => afterJoinMessage != null, Is.True.After(networkDelay, 10));
            Assert.AreEqual("Welcome!", afterJoinMessage.payload["message"].Value<string>());

            Assert.AreEqual(Channel.State.Joined, roomChannel.state);

            socket.Disconnect(null, null);

            Assert.AreEqual(Socket.State.Closed, socket.state);

            socket.Connect(string.Format("ws://{0}/phoenix_sharp_test", host), null);
            Assert.IsTrue(socket.state == Socket.State.Open);
            Assert.AreEqual(1, onOpenCount);
        }

        [Test]
        public void ReconnectTests()
        {
            var onOpenCount = 0;
            Socket.OnOpenDelegate onOpenCallback = () => onOpenCount++;

            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(socketFactory, new Socket.Options
            {
                channelRejoinInterval = TimeSpan.FromMilliseconds(200),
                logger = new BasicLogger()
            });
            socket.OnOpen += onOpenCallback;
            var url = string.Format("ws://{0}/phoenix_sharp_test", host);
            socket.Connect(url);
            Assert.IsTrue(socket.state == Socket.State.Open);
            Assert.AreEqual(1, onOpenCount);

            var afterJoinCount = 0;
            var roomChannel = socket.MakeChannel("tester:phoenix-sharp");
            roomChannel.On("after_join", m => afterJoinCount++);
            var param = new Dictionary<string, object> {
                { "auth", "doesn't matter" },
            };
            roomChannel.Join(param);
            Assert.That(() => afterJoinCount, Is.EqualTo(1).After(networkDelay, 10));

            socket.UnexpectedDisconnect();
            Assert.That(() => onOpenCount, Is.EqualTo(2).After(networkDelay + 1000, 100));
            Assert.That(() => afterJoinCount, Is.EqualTo(2).After(networkDelay, 10));
        }

        [Test]
        public void PresenceTests()
        {
            var onJoinDict = new Dictionary<string, Item>();
            var onLeaveDict = new Dictionary<string, Item>();
            var onSyncCount = 0;
            OnJoinDelegate onJoin = (string id, Item currentItem, Item newItem) => onJoinDict[id] = newItem;
            OnLeaveDelegate onLeave = (string id, Item currentItem, Item newItem) => onLeaveDict[id] = newItem;
            OnSyncDelegate onSync = () => onSyncCount++;
            var (presence1, channel1) = NewPresence("P1", onJoin, onLeave, onSync);
            Assert.That(() => GetPresenceIds(presence1), Is.EquivalentTo(new List<string> { "P1" }).After(networkDelay));

            var (presence2, channel2) = NewPresence("P2");
            Assert.That(onJoinDict.Keys, Is.EquivalentTo(new List<string> { "P1", "P2" }).After(networkDelay));
            Assert.That(GetPresenceIds(presence1), Is.EquivalentTo(new List<string> { "P1", "P2" }));
            Assert.That(GetPresenceIds(presence2), Is.EquivalentTo(new List<string> { "P1", "P2" }));

            channel2.Leave();
            Assert.That(onLeaveDict.Keys, Is.EquivalentTo(new List<string> { "P2" }).After(networkDelay));
            Assert.That(GetPresenceIds(presence1), Is.EquivalentTo(new List<string> { "P1" }));
            // P1:join, P2:join, P3:leave
            Assert.AreEqual(3, onSyncCount);
        }

        private (Presence, Channel) NewPresence(string id, OnJoinDelegate onJoin = null, OnLeaveDelegate onLeave = null, OnSyncDelegate onSync = null)
        {
            var socketFactory = new DotNetWebSocketFactory();
            var socket = new Socket(socketFactory, new Socket.Options
            {
                channelRejoinInterval = TimeSpan.FromMilliseconds(200),
                logger = new BasicLogger()
            });
            var url = string.Format("ws://{0}/phoenix_sharp_test", host);
            socket.Connect(url);
            Assert.IsTrue(socket.state == Socket.State.Open);
            var channel = socket.MakeChannel("presence");
            var presence = new Presence(channel);
            if (onJoin != null) presence.onJoin = onJoin;
            if (onLeave != null) presence.onLeave = onLeave;
            if (onSync != null) presence.onSync = onSync;

            Reply? joinOkReply = null;
            Reply? joinErrorReply = null;
            channel.Join(new Dictionary<string, object> { { "id", id } })
                .Receive(Reply.Status.Ok, r => joinOkReply = r)
                .Receive(Reply.Status.Error, r => joinErrorReply = r);
            Assert.That(() => joinOkReply.HasValue, Is.True.After(networkDelay, 10));
            return (presence, channel);
        }

        private List<string> GetPresenceIds(Presence presence)
        {
            var listIds = new List<string>();
            presence.List((id, item) => listIds.Add(id));
            return listIds;
        }
    }
}
