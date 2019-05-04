using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Naive.HttpSvr;

namespace NaiveSocks
{
    /// <summary>
    /// A simple message relay for web apps.
    /// </summary>
    class WebRelay : WebBaseAdapter
    {
        public string pass { get; set; }

        class Room
        {
            public WebRelay Parent;
            public string Token;
            public List<Client> Clients = new List<Client>();

            public Dictionary<string, string> States = new Dictionary<string, string>();

            public void Broadcast(string str, Client except)
            {
                foreach (var c in Clients) {
                    if (c == except) continue;
                    try {
                        c.SendStringAsync(str).Forget();
                    } catch (Exception e) {
                        Parent.Logger.exception(e, Logging.Level.Warning, "broadcasting to " + c.Id);
                    }
                }
            }
        }

        class Client : WebSocketServer
        {
            public Client(HttpConnection p) : base(p)
            {
            }

            public string Id;
            public string Tag = "";
            public List<Room> Joined = new List<Room>();

            public string Token
            {
                get {
                    return Id + " " + Tag;
                }
            }

            public override string ToString()
            {
                return $"{{{Id} {Tag}}}";
            }
        }

        Dictionary<string, Room> rooms = new Dictionary<string, Room>();

        public override bool Reloading(object oldInstance)
        {
            rooms = ((WebRelay)oldInstance).rooms;
            lock (rooms) {
                foreach (var r in rooms) {
                    r.Value.Parent = this;
                }
            }
            return base.Reloading(oldInstance);
        }

        public override async Task HandleRequestAsyncImpl(HttpConnection p)
        {
            if (p.ParsedQstr["pass"] != pass) return;
            if (!WebSocketServer.IsWebSocketRequest(p)) return;

            var cli = new Client(p);
            if ((await cli.HandleRequestAsync(false)).IsConnected == false) return;
            cli.Id = Guid.NewGuid().ToString("d");

            try {
                await cli.SendStringAsync("hello " + cli.Id);
                while (true) {
                    var str = await cli.RecvString();
                    var span = (Span)str;
                    var firstLine = span.CutSelf('\n');
                    var cmd = firstLine.CutSelf(' ');
                    if (cmd == "room") {
                        var verb = firstLine.CutSelf(' ');
                        var rtoken = firstLine.CutSelf(' ').ToString();
                        if (!CheckClientId(cli, ref firstLine)) continue;
                        lock (rooms) { // Use such a big lock to ensure message order.
                            rooms.TryGetValue(rtoken, out var room);
                            if (verb == "join") {
                                if (cli.Joined.Contains(room)) goto FAIL;
                                if (room == null) {
                                    rooms.Add(rtoken, room = new Room() { Token = rtoken, Parent = this });
                                }
                                if (room.Clients.Contains(cli) == false) {
                                    room.Clients.Add(cli);
                                }
                                cli.Joined.Add(room);
                                room.Broadcast(str, cli);

                                var sb = new StringBuilder();
                                sb.Append("ok room state ").Append(room.Token).Append("\n");
                                foreach (var item in room.States) {
                                    sb.Append(item.Key).Append(':').Append(item.Value).Append('\n');
                                }
                                cli.SendStringAsync(sb.ToString()).Forget();
                            } else {
                                if (room == null) goto FAIL;
                                if (cli.Joined.Contains(room) == false) goto FAIL;
                                if (verb == "msg") {
                                    room.Broadcast(str, cli);
                                    cli.SendStringAsync("ok").Forget();
                                } else if (verb == "state") {
                                    while (span.len > 0) {
                                        var line = span.CutSelf('\n');
                                        var key = line.CutSelf(':').ToString();
                                        var value = line.ToString();
                                        if (value.Length == 0) {
                                            room.States.Remove(key);
                                        } else {
                                            room.States[key] = value;
                                        }
                                    }
                                    room.Broadcast(str, cli);
                                    cli.SendStringAsync("ok").Forget();
                                } else if (verb == "getstate") {
                                    // But it's currently not needed because we actively push changes of states.
                                    var sb = new StringBuilder();
                                    sb.Append("ok room state ").Append(room.Token).Append("\n");
                                    var line = span.CutSelf('\n');
                                    if (line.len == 0) {
                                        foreach (var item in room.States) {
                                            sb.Append(item.Key).Append(':').Append(item.Value).Append('\n');
                                        }
                                    } else {
                                        while (line.len > 0) {
                                            var key = line.CutSelf(' ').ToString();
                                            if (!room.States.TryGetValue(key, out var val)) {
                                                val = "";
                                            }
                                            sb.Append(key).Append(':').Append(val).Append('\n');
                                        }
                                    }
                                    cli.SendStringAsync(sb.ToString()).Forget();
                                } else if (verb == "list") {
                                    var sb = new StringBuilder("ok list\n");
                                    foreach (var c in room.Clients) {
                                        sb.Append(c.Id).Append(' ').Append(c.Tag).Append('\n');
                                    }
                                    cli.SendStringAsync(sb.ToString()).Forget();
                                } else if (verb == "leave") {
                                    room.Clients.Remove(cli);
                                    if (room.Clients.Count == 0) {
                                        rooms.Remove(room.Token);
                                    } else {
                                        room.Broadcast(str, cli);
                                    }
                                    cli.SendStringAsync("ok").Forget();
                                } else {
                                    goto FAIL;
                                }
                            }
                        }
                    } else if (cmd == "ping") {
                        await cli.SendStringAsync("pong");
                    } else if (cmd == "tag") {
                        var verb = firstLine.CutSelf(' ');
                        if (verb == "set") {
                            var tag = firstLine.CutSelf(' ');
                            cli.Tag = tag.ToString();
                            goto OK;
                        } else {
                            goto FAIL;
                        }
                    }
                    continue;
                    OK:
                    await cli.SendStringAsync("ok");
                    continue;
                    FAIL:
                    await cli.SendStringAsync("fail");
                    continue;
                }
            } catch (Exception e) {
                Logger.exception(e, Logging.Level.Warning);
            } finally {
                lock (rooms) {
                    foreach (var room in cli.Joined) {
                        room.Clients.Remove(cli);
                        if (room.Clients.Count == 0) {
                            rooms.Remove(room.Token);
                        } else {
                            room.Broadcast($"room leave {room.Token} {cli.Token}", cli);
                        }
                    }
                    cli.Joined = null;
                }
            }
        }

        bool CheckClientId(Client cli, ref Span line)
        {
            if (line.CutSelf(' ') != cli.Id) {
                Logger.warning("message clientid check failed for " + cli);
                return false;
            }
            if (line.CutSelf(' ') != cli.Tag) {
                Logger.warning("message tag check failed for " + cli);
                return false;
            }
            return true;
        }

        struct Span
        {
            public string str;
            public int offset, len;

            public Span(string str, int offset, int len)
            {
                this.str = str;
                this.offset = offset;
                this.len = len;
            }

            public char this[int idx] => str[offset + idx];

            public static implicit operator Span(string str) => new Span(str, 0, str.Length);

            public override string ToString()
            {
                if (str == null) return "";
                if (offset == 0 && str.Length == len) return str;
                return str.Substring(offset, len);
            }

            public static bool operator ==(Span a, Span b)
            {
                if (a.len != b.len) return false;
                for (int i = 0; i < a.len; i++) {
                    if (a[i] != b[i]) return false;
                }
                return true;
            }

            public static bool operator !=(Span a, Span b) => !(a == b);

            public Span Cut(int len)
            {
                if (len < 0 || len > this.len) throw new ArgumentOutOfRangeException(nameof(len));
                return new Span(str, offset, len);
            }

            public Span CutSelf(int len)
            {
                var span = Cut(len);
                this.offset += len;
                this.len -= len;
                return span;
            }

            public Span CutSelf(char ch)
            {
                var index = str.IndexOf(ch, offset, len);
                if (index == -1) {
                    return CutSelf(this.len);
                }
                var span = new Span(str, offset, index - offset);
                this.offset += span.len + 1;
                this.len -= span.len + 1;
                return span;
            }
        }
    }
}
