﻿using Naive.Console;
using Naive.HttpSvr;
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NaiveSocks
{
    class WebconAdapter : OutAdapter, ICanReloadBetter, IHttpRequestAsyncHandler
    {
        public string passwd { get; set; }

        ConsoleHub consoleHub;

        HttpSvr httpsvr;

        public bool Reloading(object oldInstance)
        {
            if (oldInstance is WebconAdapter old) {
                httpsvr = old.httpsvr;
                consoleHub = old.consoleHub;
            }
            return false;
        }

        public override void Start()
        {
            base.Start();
            if (consoleHub == null) {
                httpsvr = new HttpSvr(this);
                consoleHub = new ConsoleHub();
                Commands.AddCommands(consoleHub.CommandHub, Controller, "");
            }
        }

        public Task HandleRequestAsync(HttpConnection p)
        {
            return httpsvr.HandleRequestAsync(p);
        }

        public override async Task HandleConnection(InConnection connection)
        {
            if (connection.Dest.Port == 80) {
                await connection.SetConnectResult(ConnectResults.Conneceted);
                HttpConnection httpConnection = NNetworkAdapter.CreateHttpConnectionFromMyStream(connection.DataStream, httpsvr);
                await httpConnection.Process();
            }
        }

        class HttpSvr : NaiveHttpServerAsync
        {
            public HttpSvr(WebconAdapter adapter)
            {
                Adapter = adapter;
            }

            public WebconAdapter Adapter { get; }

            public override Task HandleRequestAsync(HttpConnection p)
            {
                if (p.Url_path == "/admin/consolews")
                    return ws(p);
                if (p.Url_path == "/" || p.Url_path == "/webcon" || p.Url_path == "/webcon.html") {
                    p.Handled = true;
                    p.setStatusCode("200 OK");
                    return p.writeAsync(webconHtml);
                }
                return NaiveUtils.CompletedTask;
            }

            private async Task ws(HttpConnection p)
            {
                var wss = new WebSocketServer(p);
                var realPasswd = Adapter.passwd;
                var aesEnabled = false;
                void start()
                {
                    LambdaConsoleClient concli = new LambdaConsoleClient() {
                        OnWrite = (str) => {
                            wss.SendStringAsync(str);
                        }
                    };
                    wss.Received += (m) => {
                        concli.InputLine(m.data as string);
                    };
                    wss.Closed += (w) => {
                        concli.Close();
                    };
                    new Thread(() => {
                        try {
                            Adapter.consoleHub.StartSessionSelectLoop(concli);
                        } catch (Exception e) { }
                    }) { IsBackground = true, Name = "consolewsSession" }.Start();
                }
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
                        Logging.warning($"{Adapter}: wrong passwd from {p.myStream}");
                        if (--chances <= 0) {
                            await wss.SendStringAsync("session end.\r\n");
                            return;
                        }
                    }
                }
                await wss.SendStringAsync("success.\r\n");
                start();
                await wss.RecvLoopAsync().CAF();
            }
        }



        public static byte[] GetMD5FromString(string str)
        {
            using (var hash = MD5.Create())
                return hash.ComputeHash(NaiveUtils.UTF8Encoding.GetBytes(str));
        }

        const string webconHtml = @"<head>
    <meta http-equiv=""Content-Type"" content=""text/html;charset=UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1, minimum-scale=1, maximum-scale=2"">
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
    </style>
</head>

<body>
    <div class='topbar'>
        <input type=""checkbox"" id=""chkboxAutoScroll"" checked=""checked"">
        <!-- <label for=""chkboxAutoScroll"">AutoScroll</label> -->
        <input style=""width: 5em"" class='inputurl' id='inputurl' type=""text"" placeholder=""ws://..."">
        <div style=""display: flex"">
            <input id='btnconnect' type=""button"" onclick=""onConnectClick()"" value=""CONNECT"">
            <input id='btnclose' hidden type=""button"" onclick=""ws.close()"" value=""CLOSE"">
            <input id='btnopennew' type=""button"" onclick=""onOpenNewClick()"" value=""NEW"">
        </div>
    </div>
    <pre id=""consoleoutput"" style=""white-space: pre-wrap; word-break: break-word;""></pre>
    <div class='bottombar'>
        <input id=""consoleinput"" style=""width: 100%"" placeholder=""input here"" type=""text"" name=""name"" value="""" />
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
            eleInputUrl.value = url;
            console.log(""connecting to "" + url);
            ws = new WebSocket(url);
            appendText('(Connecting...)\n');
            eleBtnClose.hidden = false;
            eleBtnConnect.hidden = true;
            ws.onopen = function () {
                appendText(""connected.\n"");
                if (fisrtSendText !== null) {
                    sendInput(fisrtSendText);
                }
            };
            ws.onmessage = function (e) {
                appendText(e.data);
            };
            ws.onclose = function (e) {
                appendText('\r\nws closed.\r\n');
                eleBtnClose.hidden = true;
                eleBtnConnect.hidden = false;
            };
            ws.onerror = function (e) {
                appendText('\r\nws error.\r\n');
                window.latestError = e;
                console.log(e);
            };
            return ws;
        }
        setTimeout(function () {
            if (ws || eleInputUrl.value)
                return;
            if (window.location.protocol == 'http:' || window.location.protocol == 'https:') {
                var url = (window.location.protocol == ""https:"" ? 'wss://' : 'ws://')
                    + window.location.host + '/admin/consolews';
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
            // selfUrl.hash = ""opennew"";
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
        function appendText(str) {
            if (isFirstText) {
                isFirstText = false;
                eleOutput.textContent = '';
            }
            eleOutput.textContent += str;
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