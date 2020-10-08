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
        public string client_js_path { get; set; } = "/client.js";
        public string demo_chatroom_path { get; set; } = "/chat";

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
        Dictionary<string, Client> clients = new Dictionary<string, Client>();

        MyQueue<AuthItem> idTokens = new MyQueue<AuthItem>();

        struct AuthItem { public string Id, Token; }

        public override bool Reloading(object oldInstance)
        {
            var old = (WebRelay)oldInstance;
            rooms = old.rooms;
            clients = old.clients;
            idTokens = old.idTokens;
            lock (rooms) {
                foreach (var r in rooms) {
                    r.Value.Parent = this;
                }
            }
            return base.Reloading(oldInstance);
        }

        public override async Task HandleRequestAsyncImpl(HttpConnection p)
        {
            if (!WebSocketServer.IsWebSocketRequest(p)) {
                if (p.Url_path == client_js_path) {
                    p.Handled = true;
                    p.setStatusCode("200 OK");
                    p.setHeader(HttpHeaders.KEY_Content_Type, "text/javascript");
                    await p.EndResponseAsync(JsClient);
                    return;
                }
                if (p.Url_path == demo_chatroom_path) {
                    p.Handled = true;
                    p.setStatusCode("200 OK");
                    await p.EndResponseAsync(DemoChatRoom);
                    return;
                }
            }
            if (p.ParsedQstr["pass"] != pass) return;

            var cli = new Client(p);
            if ((await cli.HandleRequestAsync(false)).IsConnected == false) return;
            cli.Id = p.ParsedQstr["id"];
            var cliToken = p.ParsedQstr["idtoken"];
            var reuseId = false;
            if (cli.Id != null) {
                lock (rooms) {
                    foreach (var item in idTokens) {
                        if (item.Id == cli.Id) {
                            // TODO: If hit, remove and re-add to the queue.
                            if (item.Token != cliToken) break;
                            if (clients.TryGetValue(cli.Id, out var oldCli)) {
                                try {
                                    oldCli.SendStringAsync("kick-sameid").Forget();
                                } catch (Exception) { }
                                oldCli.Close();
                                RemoveClient(oldCli);
                            }
                            reuseId = true;
                            break;
                        }
                    }
                }
            }
            if (!reuseId) {
                cli.Id = Guid.NewGuid().ToString("d");
                cliToken = Guid.NewGuid().ToString("d");
            }
            lock (rooms) {
                clients.Add(cli.Id, cli);
                if (!reuseId) {
                    if (idTokens.Count >= 50) idTokens.Dequeue();
                    idTokens.Enqueue(new AuthItem { Id = cli.Id, Token = cliToken });
                }
            }
            AUTHOK:

            try {
                await cli.SendStringAsync("hello " + cli.Id + " " + cliToken);
                while (true) {
                    var str = await cli.RecvString();
                    var span = (Span)str;
                    var firstLine = span.CutSelf('\n');
                    var cmd = firstLine.CutSelf(' ');
                    if (cmd == "room") {
                        var verb = firstLine.CutSelf(' ');
                        var rtoken = firstLine.CutSelf(' ').ToString();
                        if (!CheckClientId(cli, ref firstLine)) goto FAIL;
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
                        await cli.SendStringAsync("ok pong");
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
                    RemoveClient(cli);
                }
            }
        }

        private void RemoveClient(Client cli)
        {
            if (cli.Joined == null) return;
            foreach (var room in cli.Joined) {
                room.Clients.Remove(cli);
                if (room.Clients.Count == 0) {
                    rooms.Remove(room.Token);
                } else {
                    room.Broadcast($"room leave {room.Token} {cli.Token}", cli);
                }
            }
            cli.Joined = null;
            clients.Remove(cli.Id);
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

        const string JsClient = @"
var Listeners = function () {
	this.funcs = [];
};
Listeners.prototype.add = function (func) {
	this.funcs.push(func);
};
Listeners.prototype.invoke = function () {
	for (const f of this.funcs) {
		f.apply(this, arguments);
	}
};

var WebRelayCli = function (url, options) {
	options = options || {};
	var cli = this;
	var ws = null;
	var cid = null;
	var cidToken = null;
	var tag = '';
	var idtag;
	var updateIdtag = () => idtag = cid + ' ' + tag;
	var rooms = new Map();
	var openingPromise = null;
	var replyHandlers = [];
	var emptyFunction = function () { };
	var enqueueReplyHandler = function (func) { replyHandlers.push(func || emptyFunction) };
	var promiseRequest = function (request, callback) {
		return openingPromise.then(() => new Promise((resolve, reject) => {
			if (typeof request == 'function') request = request();
			cli.send(request);
			enqueueReplyHandler((ok, s, r) => {
				console.log('request result ' + ok + ': ' + request);
				if (ok) {
					var ret = undefined;
					if (callback) ret = callback(s, r);
					resolve(ret);
				} else {
					reject();
				}
			});
		}));
	};

	var createPromise = function () {
		var resolve, reject;
		var promise = new Promise(function (res, rej) {
			resolve = res; reject = rej;
		});
		promise.resolve = resolve;
		promise.reject = reject;
		return promise;
	}

	Object.defineProperty(this, 'ws', { get: () => ws });
	Object.defineProperty(this, 'id', { get: () => cid });
	Object.defineProperty(this, 'idtoken', { get: () => cid + ' ' + cidToken });
	Object.defineProperty(this, 'tag', { get: () => tag });
	Object.defineProperty(this, 'options', { get: () => options });

	if (options.idtoken) {
		console.info('reuse idtoken: ' + options.idtoken);
		let splits = options.idtoken.split(' ');
		cid = splits[0];
		cidToken = splits[1];
	}

	var ClientId = function (room, id, tag) {
		this.room = room;
		this.id = id;
		this.tag = tag || '';
		this.self = cid == id;
	};
	ClientId.prototype.toString = function () {
		if (this.tag) {
			return '[' + this.tag + '](' + this.id + ')';
		} else {
			return '(' + this.id + ')';
		}
	}

	this.onopen = new Listeners();
	this.onclose = new Listeners();

	this.autoReconnect = 3; // null or seconds
	this.autoRejoin = true;

	this.reconnectTimes = 0;

	var parseStates = function (lines) {
		var obj = {};
		lines.pop();
		for (const l of lines) {
			let [key, value] = l.split(':');
			obj[key] = value;
		}
		return obj;
	};
	var statesToStr = function (obj) {
		var str = '';
		for (const key in obj) {
			if (obj.hasOwnProperty(key)) {
				let val = obj[key];
				if (val === null || val === undefined) val = '';
				val = val.toString();
				if (val.includes('\n')) throw new Error('values can not include newline');
				str += key + ':' + val + '\n';
			}
		}
		return str;
	};
	var mergeObject = function (source, dest) {
		for (const key in source) {
			if (source.hasOwnProperty(key)) {
				dest[key] = source[key];
			}
		}
	};

	this.open = function () {
		console.info('ws connecting...');
		openingPromise = createPromise();
		let opened = false;
		let tempUrl = url;
		if (cidToken) {
			tempUrl += tempUrl.includes('?') ? '&' : '?';
			tempUrl += 'id=' + cid + '&idtoken=' + cidToken;
		}
		ws = new WebSocket(tempUrl);
		ws.onopen = (e) => {
			console.log('ws open.');
		};
		ws.onmessage = (e) => {
			console.log('ws received: ', e.data);
			let lines = e.data.split('\n');
			let firstLine = lines.shift();
			let splits = firstLine.split(' ');
			let cmd = splits[0];
			if (cmd == 'hello') {
				cid = splits[1];
				cidToken = splits[2];
				updateIdtag();
				opened = true;
				if (this.autoRejoin && rooms.size) {
					rooms.forEach(r => r.join());
				}
				openingPromise.resolve();
				this.onopen.invoke();
			} else if (cmd == 'room') {
				let verb = splits[1];
				let rtoken = splits[2];
				let room = rooms.get(rtoken);
				let from = new ClientId(room, splits[3], splits[4]);
				if (verb == 'msg') {
					room.onmsg.invoke(lines.join('\n'), from);
				} else if (verb == 'state') {
					let changedState = parseStates(lines);
					mergeObject(changedState, room.states);
					room.onstate.invoke('remote', changedState);
				} else if (verb == 'join') {
					room.onlist.invoke('join', from);
				} else if (verb == 'leave') {
					room.onlist.invoke('leave', from);
				}
			} else if (cmd == 'ok' || cmd == 'fail') {
				let handler = replyHandlers.shift();
				handler(cmd == 'ok', splits, lines);
			} else if (cmd == 'kick-sameid') {
				cidToken = null; // stop using this id.
			}
		};
		ws.onerror = (e) => {
			console.error('ws error: ', e);
		};
		ws.onclose = (e) => {
			rooms.forEach(r => r.joined = false);
			replyHandlers.forEach(f => f(false, ['closed'], null));
			replyHandlers = [];
			if (!opened) openingPromise.reject();
			if (typeof this.autoReconnect == 'number') {
				console.info('ws reconnecting in ' + this.autoReconnect + ' seconds.');
				setTimeout(() => {
					this.reconnectTimes++;
					this.open();
				}, this.autoReconnect * 1000);
			}
			this.onclose.invoke();
		};
	};
	this.send = function (data) {
		console.log('ws send: ', data);
		ws.send(data);
	};
	this.room = function (token) {
		var r = rooms.get(token);
		if (!r) {
			r = {
				joined: false, // false, Promise or 'joined'
				token: token,
				msgEcho: false,
				states: {},
				sendmsg: function (str) {
					return promiseRequest(() => 'room msg ' + this.token + ' ' + idtag + '\n' + str, () => {
						if (this.msgEcho) {
							this.onmsg.invoke(str, new ClientId(this, cid, tag));
						}
					});
				},
				onmsg: new Listeners(),
				onstate: new Listeners(),
				onlist: new Listeners(),
				setstate: function (states) {
					var str = () => 'room state ' + this.token + ' ' + idtag + '\n' + statesToStr(states);
					return promiseRequest(str, (s, r) => {
						mergeObject(states, this.states);
						this.onstate.invoke('self', states);
					});
				},
				join: function () {
					if (this.joined == 'joined') return Promise.resolve();
					if (this.joined) return this.joined;
					return this.joined = promiseRequest(() => 'room join ' + this.token + ' ' + idtag, (s, r) => {
						if (r) this.states = parseStates(r);
						else this.states = {};
						this.joined = 'joined';
						this.onstate.invoke('init', this.states);
					}).catch(() => {
						var p = this.joined;
						this.joined = false;
						return p;
					});
				},
				list: function () {
					return promiseRequest(() => 'room list ' + this.token + ' ' + idtag, (s, r) => {
						r.pop(); // remove the last line that is empty.
						console.log('list success, members: ', r);
						let arr = r.map(x => {
							let [id, tag] = x.split(' ', 2);
							return new ClientId(this, id, tag);
						});
						this.onlist.invoke('list', arr);
						return arr;
					});
				}
			};
			rooms.set(token, r);
		}
		r.join();
		return r;
	};
	this.settag = function (newtag) {
		if (newtag.includes(' ')) return Promise.reject('cannot include space.');
		tag = newtag;
		updateIdtag();
		return promiseRequest('tag set ' + newtag);
	};
	this.ping = function () {
		let startTime = new Date().getTime();
		return promiseRequest('ping', function () {
			return new Date().getTime() - startTime;
		});
	};
	this.open();
};
";

        const string DemoChatRoom = @"<!DOCTYPE html>
<html lang='en' style='height: 100%;'>

<head>
	<meta charset='UTF-8'>
	<meta name='viewport' content='width=device-width, initial-scale=1.0'>
	<meta http-equiv='X-UA-Compatible' content='ie=edge'>
	<title>Simple Chat Room</title>
	<style>
		* {
			box-sizing: border-box;
		}

		.btn {
			display: flex;
			text-align: center;
			justify-content: center;
			align-items: center;
			transition: all .3s;
			padding: .3em .5em;
			min-width: 3em;
			line-height: 1em;
			/* margin: .5em; */
			background: hsl(207, 90%, 54%);
			color: white;
			/* border-radius: .3em; */
			box-shadow: 0 0 .3em gray;
			cursor: pointer;
			-ms-user-select: none;
			-moz-user-select: none;
			-webkit-user-select: none;
			user-select: none;
			position: relative;
			overflow: hidden;
		}

		.btn:hover {
			transition: all .05s;
			background: hsl(207, 90%, 61%);
		}

		.btn.btn-down {
			cursor: default;
		}

		.btn.btn-down,
		.btn:active {
			transition: all .05s;
			background: hsl(207, 90%, 70%);
			box-shadow: 0 0 .1em gray;
		}

		.textinput {
			box-shadow: 0 0 .2em gray;
			border: solid 1px gray;
		}

		.textinput:focus {
			box-shadow: 0 0 .2em hsl(207, 90%, 61%);
			border-color: hsl(207, 90%, 61%);
		}

		.bar {
			display: flex;
		}

		.messages {
			/* display: flex; */
			flex-direction: column;
			/* align-items: flex-start; */
			flex: 1;
			overflow-y: auto;
		}

		.msg {
			text-align: left;
			white-space: pre-wrap;
			position: relative;
			margin: .7em .2em;
		}

		.msg.info {
			text-align: center;
		}

		.msg.info .content {
			font-size: 80%;
		}

		.msg.self {
			text-align: right;
		}

		.msg .bubble {
			display: inline-block;
			position: relative;
			text-align: left;
			min-width: 5em;
			padding: .5em;
			border-radius: .3em;
			background: #81d4fa;
		}

		.msg.info .bubble {
			background: #eeeeee;
		}

		.msg.self .bubble {
			background: #c5e1a5;
		}

		.msg .sender {
			font-size: 80%;
		}

		.msg .time {
			/* position: absolute; */
			text-align: right;
			margin: 0 -.3em -.3em 0;
			color: #666;
			font-size: 70%;
		}
	</style>
</head>

<body style='display: flex; flex-direction: column; height: 100%; margin: 0; padding: .3em; font-family: sans-serif;'>
	<div class='messages' id='messages'></div>
	<div class='bar' style='height: 2em;'>
		<input class='textinput' style='flex: 1; padding: 0 .5em;' type='text' id='inputText' />
		<div class='btn' style='margin-left: .3em;' id='btnSend'>Send</div>
	</div>
	<script src='client.js'></script>
	<script>
		function Msg(text, sender) {
			this.text = text;
			this.sender = sender;
		}

		function MessagesView(ele) {
			this.ele = ele;
		}
		MessagesView.prototype.add = function (msg) {
			var p = document.createElement('div');
			var bubble = document.createElement('div');
			bubble.className = 'bubble';
			p.className = 'msg';
			var content = document.createElement('div');
			content.className = 'content';
			content.textContent = msg.text;
			var time = document.createElement('div');
			time.className = 'time';
			time.textContent = new Date().toLocaleTimeString();
			if (msg.sender) {
				if (msg.sender.self) {
					p.classList.add('self');
				} else {
					var sender = document.createElement('div');
					sender.className = 'sender';
					sender.textContent = msg.sender.toString() + ':';
					p.appendChild(sender);
				}
			} else {
				p.classList.add('info');
			}
			bubble.appendChild(content);
			bubble.appendChild(time);
			p.appendChild(bubble);
			this.ele.appendChild(p);
			p.scrollIntoView();
		};
		MessagesView.prototype.info = function (text) {
			this.add(new Msg(text));
		};

		var msgView = window.msgView = new MessagesView(document.getElementById('messages'));

		function start(url, roomname) {
			var roomname = roomname || 'simple-chat-room';
			msgView.info('Websocket: ' + url + '\nRoom: ' + roomname + '\nConnecting...');
			window.cli = new WebRelayCli(url, {
				idtoken: sessionStorage.getItem('idtoken')
			});

			var room = window.room = cli.room(roomname);
			room.msgEcho = true;

			room.onmsg.add((msg, sender) => {
				console.info('room msg: ' + msg);
				msgView.add(new Msg(msg, sender));
			});

			var listInited = false;
			room.onlist.add((action, arg) => {
				if (action == 'list') {
					arg = arg.filter(x => !x.self);
					msgView.info('Other ' + arg.length + ' members:\n' + arg.map(x => x.toString()).join('\n'));
					listInited = true;
				} else if (listInited) {
					if (action == 'join') {
						msgView.info('A member joined: ' + arg.toString());
					} else if (action == 'leave') {
						msgView.info('A member left: ' + arg.toString());
					}
				}
			});

			cli.onopen.add(function () {
				sessionStorage.setItem('idtoken', cli.idtoken);
				listInited = false;
				room.join().then(function () {
					msgView.info('You joined the room. Your ID: ' + cli.id);
					setTimeout(function () {
						room.list();
					}, 1000);
				});
			});

			cli.onclose.add(function () {
				msgView.info('Connection closed. Reconnecting...');
			});
		}
		var btn = document.getElementById('btnSend');
		var input = document.getElementById('inputText');
		btn.onclick = function () {
			var msg = input.value;
			input.value = '';
			if (msg.length) {
				if (msg.startsWith('/nick ')) {
					let newtag = msg.substr(6);
					cli.settag(newtag).then(() => {
						msgView.info('Your new nickname: ' + newtag);
					}).catch(x => {
						msgView.info('/nick error: ' + x);
					});
				} else if (window.room) {
					room.sendmsg(msg);
				} else {
					let splits = msg.split(' ');
					start(splits[0], splits[1]);
				}
			}
		};
		input.onkeypress = function (e) {
			if (e.keyCode == 13) {
				e.preventDefault();
				btn.click();
			}
		};
		(function () {
			let url, room;
			let hash = window.location.hash;
			if (hash) {
				let splits = hash.substr(1).split('&');
				url = splits[0];
				room = splits[1];
			}
			if (!url && (window.location.protocol == 'http:' || window.location.protocol == 'https:')) {
				url = (window.location.protocol == 'https:' ? 'wss://' : 'ws://')
					+ window.location.host + window.location.pathname;
			}
			if (!url) {
				msgView.info('Cannot determine connection configuration.\nPlease input: <url> [room_name]');
			} else {
				start(url, room);
			}
		})();
	</script>
</body>

</html>
";
    }
}
