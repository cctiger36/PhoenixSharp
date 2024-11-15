using System.Collections.Generic;
using NUnit.Framework;
using Phoenix;
using Newtonsoft.Json.Linq;

namespace PhoenixTests
{
    [TestFixture()]
    public class MessageSerializationTests
    {
        private Message SampleMessage
        {
            get
            {
                var payload = new Dictionary<string, object> {
                    { "some key", 12 },
                    { "another key", new Dictionary<string, object> {
                            { "nested", "value" }}},
                };

                return new Message("phoenix-test", Message.OutBoundEvent.phx_join.ToString(), "123", JObject.FromObject(payload));
            }
        }

        [Test()]
        public void SerializationTest()
        {
            var serialized = SampleMessage.Serialize();
            var expected = "{"
                + "\"topic\":\"phoenix-test\","
                + "\"event\":\"phx_join\","
                + "\"ref\":\"123\","
                + "\"payload\":{\"some key\":12,\"another key\":{\"nested\":\"value\"}}"
                + "}";

            Assert.AreEqual(serialized, expected);
        }

        [Test()]
        public void DeserializationTest()
        {
            var serialized = SampleMessage.Serialize();
            var deserialized = MessageSerialization.Deserialize(serialized);

            Assert.AreEqual(deserialized, SampleMessage);
            Assert.IsInstanceOf(typeof(JObject), deserialized.payload["another key"]);
        }

        [Test()]
        public void SerializingNullPayloadTest()
        {
            var message = new Message(null, null, null, null);
            Assert.IsNotNull(message.payload); // consistent with phoenix.js
        }
    }
}
