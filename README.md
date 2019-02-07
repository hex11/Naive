[**Download Releases**](https://github.com/hex11/Naive/releases)
or
[Latest build on Appveyor](https://ci.appveyor.com/project/hex11/naive/build/artifacts)

Build status (latest commit):

[![Appveyor build status](https://ci.appveyor.com/api/projects/status/brdxtqcek50ny38b?svg=true)](https://ci.appveyor.com/project/hex11/naive)

Build status (master branch):

[![Appveyor build status](https://ci.appveyor.com/api/projects/status/brdxtqcek50ny38b/branch/master?svg=true)](https://ci.appveyor.com/project/hex11/naive/branch/master)


# Feature & Future

- [x] Interactive CLI
- [x] Configuration file using TOML
- [x] Supported protocols
  - [x] 'direct' (in/out)
  - [x] 'socks5' (in/out)
  - [x] 'http' (in/out)
  - [x] 'tlssni' (in)
  - [x] 'ss' (in/out)
  - [x] 'naive' (in/out)
    - [x] Multiplexing (for 0-RTT handshake) and optional inversed multiplexing (for bandwidth)
    - [x] Optional compression
- [x] 'dns' - simple DNS server (UDP) and client (UDP/DoH)
- [x] 'router'
  - [x] [ABP Filter](https://adblockplus.org/filter-cheatsheet) support
  - [x] Auto download ABP Filters from URL
- [x] Web server
  - [x] 'webfile' - Static website / file server
  - [x] 'webcon' - Web console
  - [ ] Advanced configuration UI
- [x] Linux
  - [x] Async socket implementation using epoll
  - [x] 'tproxy' (in) - Transparent proxy support
- [x] Android
  - [x] UI: Status / Adapters / Connections / Log / Console
  - [x] Simple text configuration editor
  - [ ] Advanced configuration UI
  - [x] VpnService implementation using tun2socks
- [ ] Windows
  - [ ] GUI with systray
- [ ] UDP support
  - [ ] 'direct'
  - [ ] 'socks5'
  - [ ] 'naive'
