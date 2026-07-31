# File Browser

A lightweight ASP.NET Core file browser built for the MapLarge internship
exercise. The frontend uses vanilla JavaScript with no framework or build step.

## Run

Requires .NET 8 or newer.

```powershell
dotnet run
```

The home directory and limits are configured in `appsettings.json`.

## Code to review

- `FileSystem/FileSystemService.cs` — path validation and all filesystem work
- `Program.cs` — Minimal API endpoints, errors, configuration, and timing
- `wwwroot/app.js` — routing, rendering, dialogs, caching, and UI behavior
- `wwwroot/api.js` — API requests
- `FileSystem/FileModels.cs` — compact request and response records
- `wwwroot/index.html` and `wwwroot/styles.css` — interface structure and styling

The main original implementation is in `FileSystem/FileSystemService.cs` and
`wwwroot/app.js`.

## Features

- Browse and recursively search files and folders
- Upload, download, create, delete, move, and copy
- Deep-linkable navigation in a native dialog
- File/folder counts and visible file sizes

All request paths are normalized and confined to the configured home directory.
Uploads, downloads, and copies are streamed; browsing never calculates
recursive folder sizes, and search is iterative, cancellable, and limited.

Search matches names only. Authentication is outside this proof of concept.
