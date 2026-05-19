# Vision Language Agent 🤖👁️

![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS-lightgray.svg)
![Ollama](https://img.shields.io/badge/Powered%20by-Ollama%20(Llama%203.2%20Vision)-blue.svg)

**Vision Language Agent** is a lightweight, cross-platform autonomous tool that scans your local directories for images and intelligently renames them based on their visual content using a local Large Vision Model (LVM).

No cloud APIs. No privacy concerns. 100% local processing.

## 🌟 Features

*   **Intelligent Image Renaming**: Uses the `llama3.2-vision` model to "look" at your images and generate concise, descriptive filenames (e.g., `black-motorcycle.jpg`, `coffee-cup.png`).
*   **100% Local & Private**: Powered by [Ollama](https://ollama.com/). Your images never leave your machine.
*   **Cross-Platform**: Works seamlessly on Windows and macOS (Intel & Apple Silicon).
*   **Native Folder Picker**: Uses lightweight native OS dialogs for folder selection without the heavy footprint of UI frameworks like WPF or Electron.
*   **Smart Lifecycle Management**: 
    *   Automatically checks if Ollama is running; if not, it starts it in the background.
    *   Auto-downloads (`pull`) the required model if it's not installed.
    *   Frees up VRAM automatically when the application closes.
*   **High Performance**: Processes multiple images concurrently with built-in concurrency limits to prevent memory/VRAM overload.
*   **Collision Handling**: Automatically handles duplicate names (e.g., `white-cat_1.jpg`) without overwriting existing files.

## 🚀 How It Works

1.  Launch the application.
2.  A native folder picker dialog opens. Select the folder containing your `.jpg` or `.png` images.
3.  The agent verifies the Ollama service and the `llama3.2-vision` model.
4.  It scans the directory recursively and starts analyzing the images in the background.
5.  Files are renamed in place.
6.  Once done (or upon exit), the agent gracefully unloads the model from your VRAM.

## 🛠️ Prerequisites

*   [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (If running from source)
*   [Ollama](https://ollama.com/download) installed on your system.

## 💻 Building and Publishing

You can easily compile the project into a **single, self-contained executable** that doesn't even require the user to have .NET installed!

Run the following commands in the project directory based on your target OS:

### Windows (x64)
```powershell
dotnet publish -c Release -r win-x64 /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true
```

### macOS (Apple Silicon / M1, M2, M3)
```bash
dotnet publish -c Release -r osx-arm64 /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true
```

### macOS (Intel)
```bash
dotnet publish -c Release -r osx-x64 /p:PublishSingleFile=true /p:SelfContained=true /p:IncludeNativeLibrariesForSelfExtract=true
```

The compiled binaries will be located in: `bin/Release/net10.0/<target-runtime>/publish/`

## 📦 Dependencies

*   `Microsoft.Extensions.Hosting`
*   `Microsoft.Extensions.Http`
*   `NativeFileDialogSharp` (For cross-platform native folder selection)

## 🤝 Contributing

Contributions, issues, and feature requests are welcome!
Feel free to check the [issues page](../../issues).

## 📝 License

This project is open-source and available under the [MIT License](LICENSE).
