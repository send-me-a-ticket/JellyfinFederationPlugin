# Jellyfin Federation Plugin

## 🚧 under construction, come back later (or contribute) 🚧

### Early Build - August 25, 2025

So I was able to get the plugin on a somewhat stable state..
More testing and contributions are needed to reach alpha.


## Installation Instructions

### 1. Load Manifest
name: `Jellyfin Federation Manifesto`

url: `https://github.com/send-me-a-ticket/JellyfinFederationPlugin/raw/refs/heads/main/manifest.json`

### 2. Install Plugin
refresh your plugin library, you will find "Jellyfin Federation Plugin" on the list.

### 3. Configure
setup configurations, enable or disable server modes, client modes. add peer server URLs and their API codes.

---


## Build Instructions


### Tools Needed
`Visual Studio 2022` or `Visual Studio Code`, `C# SDK`


### get md5 checksum
`certutil -hashfile Release.zip MD5`

---

### generate build file
`dotnet build -c release`

---
### DISCLAIMER

This is still very much a work in progress. If the code somehow melts your CPU or summons any kind of ancient evil, I am not liable for damages- digital, physical, mental or anything else.

> ☭ united federation of jellyfin








