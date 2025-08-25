
# Jellyfin Federation Plugin
**🚧 UNDER CONSTRUCTION 🚧**
*( please come back later or contribute )*

*⚠️ DISCLAIMER : *
This is still very much a work in progress. If the code somehow melts your CPU or summons any kind of ancient evil, I am not liable for damages- digital, physical, mental or anything else.

---

### Early Build Now Available - August 25, 2025

So I was able to get the plugin on a somewhat functional state.. lot more testing and contributions are needed to reach alpha.

Please do not install on production systems, your library items may be irreversibly damaged or broken.


## 🛠️ Installation Instructions

### 1. Add manifest to Jellyfin catalog
name: `Jellyfin Federation Manifesto`

url: `https://github.com/send-me-a-ticket/JellyfinFederationPlugin/raw/refs/heads/main/manifest.json`

### 2. Install
Refresh your plugin library, The "Jellyfin Federation Plugin" will now be available for installation.

### 3. Configure
After installation, access the plugin's configuration page. Here, you can setup configurations, enable or disable server/client modes, add peer server URLs with API codes, etc.


## ⚙️ Build Instructions


### Tools Needed
`Visual Studio 2022` or `Visual Studio Code`,
`.NET 8.0`

### Get MD5 Checksum
`certutil -hashfile Release.zip MD5`

### Generate Build File
`dotnet build -c release`

> ☭ united federation of jellyfin





