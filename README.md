[**Download Releases**](https://github.com/hex11/Naive/releases)
or
[Latest build on Appveyor](https://ci.appveyor.com/project/hex11/naive/build/artifacts)

Build status:

[![Appveyor build status](https://ci.appveyor.com/api/projects/status/brdxtqcek50ny38b?svg=true)](https://ci.appveyor.com/project/hex11/naive)

Build status (master branch):

[![Appveyor build status](https://ci.appveyor.com/api/projects/status/brdxtqcek50ny38b/branch/master?svg=true)](https://ci.appveyor.com/project/hex11/naive/branch/master)
[![Travis CI build status](https://travis-ci.org/hex11/Naive.svg?branch=master)](https://travis-ci.org/hex11/Naive)


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
    - [x] Multiplexing
    - [x] Inversed multiplexing
    - [x] Compression
- [x] Router
  - [x] ABP Filter support
  - [x] Auto download ABP Filters from URL
- [x] Web server
  - [x] 'webfile' - Static website / file server
  - [x] 'webcon' - Web console
  - [ ] Advanced configuration UI
- [x] Linux
  - [x] Async socket implementation using epoll
- [x] Android
  - [x] UI: Status / Adapters / Connections / Log / Console
  - [x] Simple text configuration editor
  - [ ] Advanced configuration UI
  - [x] VpnService implementation
    - [x] Using tun2socks
    - [x] Local DNS
      - [x] Using LiteDB
- [ ] Windows
  - [ ] GUI with systray
- [ ] UDP support
  - [ ] 'direct'
  - [ ] 'socks5'
  - [ ] 'naive'
