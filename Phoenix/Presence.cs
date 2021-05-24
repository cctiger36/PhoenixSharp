using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Phoenix
{
    public class Presence
    {
        public class Item : Dictionary<string, List<Dictionary<string, string>>> { };
        public class State : Dictionary<string, Item> { };
        public class Diff : Dictionary<string, State> { };

        private static readonly Dictionary<string, string> events = new Dictionary<string, string>()
        {
            {"state", "presence_state"},
            {"diff", "presence_diff"}
        };

        public Channel channel;
        private string joinRef;
        private State state = new State();
        private List<Diff> pendingDiffs = new List<Diff>();

        public delegate void OnJoinDelegate(string id, Item currentItem, Item newItem);
        public OnJoinDelegate onJoin;
        public delegate void OnLeaveDelegate(string id, Item currentItem, Item newItem);
        public OnLeaveDelegate onLeave;
        public delegate void OnSyncDelegate();
        public OnSyncDelegate onSync;

        public Presence(Channel channel)
        {
            this.channel = channel;
            this.channel.On(events["state"], msg =>
            {
                joinRef = channel.joinRef;
                var newState = msg.payload.ToObject<State>();
                state = SyncState(state, newState, onJoin, onLeave);
                pendingDiffs.ForEach(diff =>
                {
                    state = SyncDiff(state, diff, onJoin, onLeave);
                });
                pendingDiffs = new List<Diff>();
                onSync?.Invoke();
            });
            this.channel.On(events["diff"], msg =>
            {
                var diff = msg.payload.ToObject<Diff>();
                if (InPendingSyncState())
                {
                    pendingDiffs.Add(diff);
                }
                else
                {
                    state = SyncDiff(state, diff, onJoin, onLeave);
                    onSync?.Invoke();
                }
            });
        }

        private bool InPendingSyncState()
        {
            return joinRef == null || (joinRef != channel.joinRef);
        }

        public List<Item> list(Action<string, Item> by)
        {
            return list(state, by);
        }

        public static State SyncState(State currentState, State newState, OnJoinDelegate onJoin, OnLeaveDelegate onLeave)
        {
            var state = Clone(currentState);
            var joins = new State();
            var leaves = new State();
            foreach (var entry in state)
            {
                if (!newState.ContainsKey(entry.Key))
                {
                    leaves[entry.Key] = entry.Value;
                }
            }
            foreach (var entry in newState)
            {
                if (state.ContainsKey(entry.Key))
                {
                    var currentItem = state[entry.Key];
                    var newRefs = entry.Value["metas"].Select(m => m["phx_ref"]).ToList();
                    var curRefs = currentItem["metas"].Select(m => m["phx_ref"]).ToList();
                    var joinedMetas = entry.Value["metas"].Where(m => !curRefs.Contains(m["phx_ref"])).ToList();
                    var leftMetas = currentItem["metas"].Where(m => !newRefs.Contains(m["phx_ref"])).ToList();
                    if (joinedMetas.Count() > 0)
                    {
                        joins[entry.Key] = entry.Value;
                        joins[entry.Key]["metas"] = joinedMetas;
                    }
                    if (leftMetas.Count() > 0)
                    {
                        leaves[entry.Key] = Clone(currentItem);
                        leaves[entry.Key]["metas"] = leftMetas;
                    }
                }
                else
                {
                    joins[entry.Key] = entry.Value;
                }
            }
            var diff = new Diff();
            diff["joins"] = joins;
            diff["leaves"] = leaves;
            return SyncDiff(state, diff, onJoin, onLeave);
        }

        public static State SyncDiff(State currentState, Diff diff, OnJoinDelegate onJoin, OnLeaveDelegate onLeave)
        {
            var state = Clone(currentState);
            foreach (var entry in diff["joins"])
            {
                Item currentItem = null;
                if (state.ContainsKey(entry.Key)) currentItem = state[entry.Key];
                state[entry.Key] = entry.Value;
                if (currentItem != null)
                {
                    var joinedRefs = state[entry.Key]["metas"].Select(m => m["phx_ref"]).ToList();
                    var curMetas = currentItem["metas"].Where(m => !joinedRefs.Contains(m["phx_ref"])).ToList();
                    state[entry.Key]["metas"] = curMetas.Concat(state[entry.Key]["metas"]).ToList();
                }
                onJoin?.Invoke(entry.Key, currentItem, entry.Value);
            }
            foreach (var entry in diff["leaves"])
            {
                if (!state.ContainsKey(entry.Key)) { continue; }
                var currentItem = state[entry.Key];
                var refsToRemove = entry.Value["metas"].Select(m => m["phx_ref"]).ToList();
                currentItem["metas"] = currentItem["metas"].Where(m => !refsToRemove.Contains(m["phx_ref"])).ToList();
                onLeave?.Invoke(entry.Key, currentItem, entry.Value);
                if (currentItem["metas"].Count() == 0)
                {
                    state.Remove(entry.Key);
                }
            }
            return state;
        }

        public static List<Item> list(State presenses, Action<string, Item> chooser)
        {
            if (chooser == null) return presenses.Values.ToList();
            return presenses.Select(entry =>
            {
                chooser(entry.Key, entry.Value);
                return entry.Value;
            }).ToList();
        }

        private static T Clone<T>(T original)
        {
            return JObject.FromObject(original).ToObject<T>();
        }
    }
}