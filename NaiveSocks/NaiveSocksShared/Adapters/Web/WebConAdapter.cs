using Naive.Console;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class WebConAdapter : WebBaseAdapter, ICanReload
    {
        public string passwd { get; set; }
        public bool no_passwd { get; set; }

        public string[] page_path { get; set; } = new[] { "/", "/webcon", "/webcon.html" };
        public string[] ws_path { get; set; } = new[] { "/admin/consolews", "/webcon.ws" };

        public string page_file { get; set; }

        public string[] cmds { get; set; }

        ConsoleHub consoleHub;

        public override bool Reloading(object oldInstance)
        {
            base.Reloading(oldInstance);
            if (oldInstance is WebConAdapter old) {
                consoleHub = old.consoleHub;
            }
            return false;
        }

        protected override void OnStart()
        {
            base.OnStart();
            if (passwd.IsNullOrEmpty() && no_passwd == false) {
                Logger.warning("'passwd' is null or empty while 'no_passwd' is false!");
            }
            if (consoleHub == null) {
                consoleHub = new ConsoleHub();
                var cmdHub = consoleHub.CommandHub;
                Commands.AddCommands(cmdHub, Controller, "", cmds);
            }
        }

        public override Task HandleRequestAsyncImpl(HttpConnection p)
        {
            if (ws_path.Contains(p.Url_path))
                return ws(p);
            if (page_path.Contains(p.Url_path)) {
                p.Handled = true;
                p.setStatusCode("200 OK");
                if (page_file != null && File.Exists(page_file)) {
                    return writePageFile(p);
                } else {
                    return p.writeAsync(webconHtml);
                }
            }
            return NaiveUtils.CompletedTask;
        }

        private async Task writePageFile(HttpConnection p)
        {
            using (var fs = File.OpenRead(page_file)) {
                await p.HandleSeekableStreamAsync(fs);
            }
        }

        class ConClient : ConsoleClient
        {
            private readonly WebSocketServer wss;

            public ConClient(WebSocketServer wss)
            {
                this.wss = wss;
                wss.Received += (m) => {
                    if (m.data is string text) {
                        InputLine(text);
                    }
                };
                wss.Closed += (w) => {
                    Close();
                };
            }

            public override void Write(string text)
            {
                if (ColorEnabled) {
                    var c = ForegroundColor32;
                    wss.SendBytesAsync(new byte[] { 0x01, c.R, c.G, c.B, c.A });
                    wss.SendStringAsync(text).Forget();
                } else {
                    wss.SendStringAsync(text).Forget();
                }
            }
        }

        private async Task ws(HttpConnection p)
        {
            var wss = new WebSocketServer(p);
            if (!no_passwd) {
                var realPasswd = passwd;
                var aesEnabled = false;
                wss.AddToManaged();
                if (!(await wss.HandleRequestAsync(false).CAF()).IsConnected)
                    return;
                if (p.ParseUrlQstr()["encryption"] == "1") {
                    wss.ApplyAesStreamFilter(GetMD5FromString(realPasswd));
                    await wss.StartVerify(true).CAF();
                }
                int chances = 3;
                while (true) {
                    await wss.SendStringAsync("passwd:\r\n");
                    var passwd = await wss.RecvString();
                    if (passwd == null)
                        return;
                    if (!aesEnabled && passwd == "__AesStreamFilter__") {
                        wss.ApplyAesStreamFilter(GetMD5FromString(realPasswd));
                        await wss.StartVerify(false);
                        continue;
                    }
                    if (passwd == realPasswd) {
                        break;
                    } else {
                        Logger.warning($"{(passwd.Length == 0 ? "empty" : "wrong")} passwd from {p.myStream}");
                        if (--chances <= 0) {
                            await wss.SendStringAsync("session end.\r\n");
                            return;
                        }
                    }
                }
                await wss.SendStringAsync("success.\r\n");
            } else {
                await wss.SendStringAsync("success (no passwd).\r\n");
            }

            var concli = new ConClient(wss);
            new Thread(() => {
                try {
                    consoleHub.SessionSelectLoop(concli);
                } catch (Exception e) { }
            }) { IsBackground = true, Name = "consolewsSession" }.Start();

            await wss.RecvLoopAsync().CAF();
        }

        public static byte[] GetMD5FromString(string str)
        {
            using (var hash = MD5.Create())
                return hash.ComputeHash(NaiveUtils.UTF8Encoding.GetBytes(str));
        }

        const string webconHtml = @"<head>
	<meta http-equiv='Content-Type' content='text/html;charset=UTF-8'>
	<meta name='viewport' content='width=device-width, initial-scale=1, minimum-scale=1, maximum-scale=2'>
	<style>
		body,
		input {
			background: white;
			color: rgb(50, 50, 50);
		}

		input {
			padding: 2px;
			transition: border .3s;
			border: 1px solid rgb(50, 50, 50);
			color: rgb(50, 50, 50);
		}

		input:focus {
			border: 1px solid black;
			color: black;
		}

		.topbar,
		.bottombar {
			position: sticky;
			padding: 2px;
			background: white;
			opacity: 0.7;
		}

		.bottombar:focus{
			opacity: 1;
		}

		.topbar {
			top: 0;
			display: -webkit-flex;
			display: flex;
			flex-wrap: wrap;
		}

		.inputurl {
			-webkit-flex: 1;
			flex: 1;
		}

		.bottombar {
			bottom: 0;
		}

		#consoleoutput {
			white-space: pre-wrap;
			word-break: break-word;
			background-color: black;
			color: lightgray;
			font-family: Consolas, monospace;
			font-size: 13px;
			line-height: 1em;
			margin: 2px;
			padding: 2px;
		}
	</style>
</head>

<body>
	<div class='topbar'>
		<input type='checkbox' id='chkboxAutoScroll' checked='checked'>
		<!-- <label for='chkboxAutoScroll'>AutoScroll</label> -->
		<input style='width: 5em' class='inputurl' id='inputurl' type='text' placeholder='ws://...'>
		<div style='display: flex'>
			<input id='btnconnect' type='button' onclick='onConnectClick()' value='CONNECT'>
			<input id='btnclose' hidden type='button' onclick='ws.close()' value='CLOSE'>
			<input id='btnopennew' type='button' onclick='onOpenNewClick()' value='NEW'>
		</div>
	</div>
	<pre id='consoleoutput'></pre>
	<div class='bottombar'>
		<input id='consoleinput' style='width: 100%' placeholder='input here' type='text' name='name' value='' />
	</div>
	<div id='outputend'></div>
	<script>
		window.onerror = function (e) {
			alert(e);
		};
		var query = !function () {
			var esc = encodeURIComponent;
			return function (params) {
				Object.keys(params)
					.map(k => esc(k) + '=' + esc(params[k]))
					.join('&');
			}
		}();
		var ws = null;
		var eleBtnClose = document.getElementById('btnclose');
		var eleBtnConnect = document.getElementById('btnconnect');
		var eleChkboxAutoScroll = document.getElementById('chkboxAutoScroll');
		var eleInputUrl = document.getElementById('inputurl');
		var eleInput = document.getElementById('consoleinput');
		var eleOutput = document.getElementById('consoleoutput');
		var eleOutputEnd = document.getElementById('outputend');
		function connectTo(url) {
			var msgStyle = 'display: block; text-align: center; background: #666; color: #fff; font-size: 120%; line-height: 1.2em;';
			var msg = function (text) {
				appendText(text, msgStyle);
			};
			eleInputUrl.value = url;
			console.log('connecting to ' + url);
			ws = new WebSocket(url);
			msg('(connecting...)');
			eleBtnClose.hidden = false;
			eleBtnConnect.hidden = true;
			ws.binaryType = 'arraybuffer';
			ws.onopen = function () {
				msg('(connected)');
				if (fisrtSendText !== null) {
					sendInput(fisrtSendText);
				}
			};
			var nextstyle = null;
			ws.onmessage = function (e) {
				var data = e.data;
				if (typeof data == 'string') {
					appendText(data, nextstyle);
					nextstyle = null;
				} else {
					var dv = new DataView(data);
					var op = dv.getUint8(0);
					switch(op) {
					case 1:
						var r = dv.getUint8(1);
						var g = dv.getUint8(2);
						var b = dv.getUint8(3);
						var a = dv.getUint8(4) / 255;
						nextstyle = 'color: rgba(' + r + ',' + g + ',' + b + ',' + a + ')';
						break;
					}
				}
			};
			ws.onclose = function (e) {
				msg('(closed)');
				eleBtnClose.hidden = true;
				eleBtnConnect.hidden = false;
			};
			ws.onerror = function (e) {
				msg('(error)');
				window.latestError = e;
				console.log(e);
			};
			return ws;
		}
		setTimeout(function () {
			if (ws || eleInputUrl.value)
				return;
			if (window.location.protocol == 'http:' || window.location.protocol == 'https:') {
				var url = (window.location.protocol == 'https:' ? 'wss://' : 'ws://')
					+ window.location.host + '/webcon.ws';
				eleInputUrl.value = url;
				// ws = connectTo(url);
			}
		}, 100);
		function onConnectClick() {
			ws = connectTo(eleInputUrl.value);
		}
		function onOpenNewClick() {
			var url = eleInputUrl.value;
			var selfUrl = window.location;
			// selfUrl.hash = 'opennew';
			var newWindow = window.open(selfUrl.toString());
			console.log(newWindow);
			if (newWindow == null)
				return;
			newWindow.onload = function () {
				newWindow.fisrtSendText = fisrtSendText;
				newWindow.connectTo(url);
			}
		}
		var fisrtSendText = null;
		function sendInput(str) {
			if (fisrtSendText === null) {
				fisrtSendText = str;
			}
			ws.send(str);
		}
		var isFirstText = true;
		function appendText(str, style) {
			if (isFirstText) {
				isFirstText = false;
				eleOutput.innerHTML = '';
			}
			if (style) {
				var span = document.createElement('span');
				span.textContent = str;
				span.style = style;
				eleOutput.appendChild(span);
			} else {
				eleOutput.appendChild(document.createTextNode(str));
			}
			if (eleChkboxAutoScroll.checked)
				setTimeout(function () { eleOutputEnd.scrollIntoView(false); }, 1);
		}
		eleInput.onkeydown = function (e) {
			if (e.which == 13 || e.keyCode == 13) {
				if (!ws) {
					onConnectClick();
					return;
				}
				sendInput(eleInput.value);
				eleInput.value = '';
			}
		};    
	</script>
</body>
";
    }
}
