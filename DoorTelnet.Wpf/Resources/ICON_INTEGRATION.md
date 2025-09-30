# DoorTelnet Icon Integration Guide

## Generated Icon Files
After generating the icon using the prompt in `ICON_GENERATION_PROMPT.md`, you'll need to integrate it into the WPF application.

## File Structure
The following icon files should be placed in the `DoorTelnet.Wpf/Resources/` directory:

```
DoorTelnet.Wpf/
??? Resources/
?   ??? app_icon.ico          # Main application icon (all sizes)
?   ??? app_icon_16.png       # 16x16 PNG version
?   ??? app_icon_24.png       # 24x24 PNG version  
?   ??? app_icon_32.png       # 32x32 PNG version
?   ??? app_icon_48.png       # 48x48 PNG version
?   ??? app_icon_64.png       # 64x64 PNG version
?   ??? app_icon_128.png      # 128x128 PNG version
?   ??? app_icon_256.png      # 256x256 PNG version
```

## Integration Steps

### 1. Add Icon to Project File
The `DoorTelnet.Wpf.csproj` file needs to be updated to include the icon:

```xml
<PropertyGroup>
  <OutputType>WinExe</OutputType>
  <TargetFramework>net8.0-windows</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <UseWPF>true</UseWPF>
  <ApplicationIcon>Resources\app_icon.ico</ApplicationIcon>
</PropertyGroup>
```

### 2. Add Icon to MainWindow
Update `MainWindow.xaml` to include the icon:

```xml
<Window x:Class="DoorTelnet.Wpf.MainWindow"
        Icon="Resources/app_icon.ico"
        Title="DoorTelnet"
        ...>
```

### 3. Include Resources in Project
Add to the `DoorTelnet.Wpf.csproj` file:

```xml
<ItemGroup>
  <Resource Include="Resources\app_icon.ico" />
  <Resource Include="Resources\app_icon_16.png" />
  <Resource Include="Resources\app_icon_24.png" />
  <Resource Include="Resources\app_icon_32.png" />
  <Resource Include="Resources\app_icon_48.png" />
  <Resource Include="Resources\app_icon_64.png" />
  <Resource Include="Resources\app_icon_128.png" />
  <Resource Include="Resources\app_icon_256.png" />
</ItemGroup>
```

## Usage Locations
The icon will appear in:
- **Window Title Bar**: Shows in the top-left corner of the application window
- **Taskbar**: Shows when the application is running
- **Alt+Tab**: Shows in the application switcher
- **File Explorer**: Shows for the executable file
- **Start Menu/Desktop**: Shows for shortcuts

## Testing
After integration, test the icon appears correctly in:
1. Visual Studio designer (MainWindow.xaml)
2. Running application window
3. Taskbar when application is running
4. Built executable file in File Explorer

## Notes
- The ICO file should contain all sizes for optimal display across different contexts
- PNG files can be used for design iteration and testing
- Ensure the icon looks good on both light and dark backgrounds
- Test readability at 16x16 pixels (smallest taskbar size)