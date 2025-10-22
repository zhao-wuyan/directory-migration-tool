# Changelog

All notable changes to this project will be documented in this file.

## [2.0.0] - 2024-10-22

### Added - GUI Version (WinUI 3)

#### MigrationCore Library
- **Models Package**
  - `MigrationConfig` - Configuration for migration parameters
  - `FileStats` - File statistics (count, size, large files)
  - `MigrationProgress` - Real-time progress information
  - `MigrationResult` - Migration result with success/error status

- **Services Package**
  - `MigrationService` - Core migration service with 6-phase execution
  - `PathValidator` - Path validation with blocked/warning lists
  - `FileStatsService` - File scanning and statistics calculation
  - `SymbolicLinkHelper` - Symbolic link creation via P/Invoke and cmd

#### MoveWithSymlinkGUI Application
- **MVVM Architecture**
  - `MainViewModel` - Complete business logic with data binding
  - Command pattern using CommunityToolkit.Mvvm
  - Real-time property updates

- **4-Step Wizard UI**
  - Step 1: Path selection with folder picker
  - Step 2: Directory scanning with statistics
  - Step 3: Migration execution with real-time progress
  - Step 4: Completion result display

- **UI Features**
  - Modern WinUI 3 Fluent Design
  - Real-time progress bar with percentage
  - Scrollable log window with timestamps
  - Step navigation indicator
  - InfoBar for errors and warnings
  - Cancel operation support
  - Folder browser integration

#### Safety Features
- Blocked system directories validation
- Cloud sync folder warnings
- Administrator permission detection
- Disk space verification (with 10% buffer)
- Automatic rollback on failure
- Copy integrity verification

#### Developer Experience
- Build script (`build.ps1`) for one-click building
- Comprehensive documentation in Chinese
- Project structure diagram
- Quick reference card
- Launch settings and publish profiles

### Technical Stack
- .NET 8.0 (Windows)
- WinUI 3 (Windows App SDK 1.5)
- CommunityToolkit.Mvvm 8.2
- P/Invoke for symbolic link creation
- Robocopy for file copying

### Documentation
- `README.md` - English project overview
- `使用说明.md` - Detailed Chinese user guide
- `项目结构.md` - Technical architecture documentation
- `完成说明.md` - Implementation summary
- `快速参考.md` - Quick reference card
- `CHANGELOG.md` - This file

---

## [1.0.0] - Initial Release

### Features - PowerShell CLI Version
- Command-line interface with parameters
- Real-time progress bar in console
- File scanning and statistics
- Robocopy mirror copying with multi-threading
- Symbolic link creation via mklink
- Automatic rollback on failure
- Large file threshold detection
- Administrator permission check
- Disk space verification

### Core Functions
- Path validation and canonicalization
- File statistics calculation (size, count, large files)
- Progress monitoring with speed and ETA
- Safe directory migration with backup
- Symbolic link health check
- Automatic cleanup of backup directory

### Parameters
- `-Source` - Source directory path (required)
- `-Target` - Target directory path (required)
- `-LargeFileThresholdMB` - Large file threshold (default: 1024 MB)
- `-RobocopyThreads` - Number of robocopy threads (default: 8)
- `-SampleMilliseconds` - Progress sampling interval (default: 1000 ms)

### Safety Mechanisms
- System critical directory blocking
- Path relation validation (no circular references)
- Backup before switching to symbolic link
- Rollback on any failure
- Copy size verification

---

## Future Enhancements (Planned)

### Features
- [ ] Batch migration queue
- [ ] Filter rules (exclude patterns)
- [ ] Hash verification (MD5/SHA256)
- [ ] Structured logging (JSON/CSV export)
- [ ] Cloud sync integration with pause option
- [ ] Idle time execution scheduler
- [ ] Migration history tracking
- [ ] Multi-language support (i18n)

### Technical Improvements
- [ ] Unit test coverage
- [ ] Integration tests
- [ ] Serilog structured logging
- [ ] Configuration file support
- [ ] MSIX packaging for GUI
- [ ] Auto-update mechanism
- [ ] Performance profiling
- [ ] Memory optimization for large directories

### UI Enhancements
- [ ] Dark/Light theme toggle
- [ ] Custom color schemes
- [ ] Migration templates
- [ ] Drag-and-drop support
- [ ] Mini mode (compact view)
- [ ] System tray integration
- [ ] Toast notifications

---

## Version History

| Version | Date | Description |
|---------|------|-------------|
| 2.0.0 | 2024-10-22 | WinUI 3 GUI version with MVVM architecture |
| 1.0.0 | Initial | PowerShell CLI version |

---

## Breaking Changes

### From 1.0.0 to 2.0.0
- None (PowerShell CLI remains compatible)
- New GUI application requires .NET 8 and Windows App SDK
- Core logic extracted to separate library (MigrationCore)

---

## Migration Guide

### Upgrading from CLI to GUI
The PowerShell CLI version (`MoveWithSymlink.ps1`) continues to work as before. The GUI version is a complementary addition, not a replacement.

**To use the GUI version:**
1. Install .NET 8.0 Runtime if not already installed
2. Build the solution using `.\build.ps1`
3. Run `MoveWithSymlinkGUI.exe` with administrator privileges

**CLI remains available for:**
- Automation and scripting
- Batch processing
- Remote execution
- Non-interactive scenarios

---

## Known Issues

### Version 2.0.0
- GUI requires administrator privileges (or Developer Mode)
- Large directory scans (>1 million files) may take significant time
- Progress percentage may jump near completion due to robocopy behavior
- Log window does not auto-scroll to bottom (requires manual scroll)

### Workarounds
- **Admin privileges**: Enable Developer Mode in Windows Settings
- **Large scans**: Be patient, or use CLI version with output redirection
- **Progress jumps**: This is expected behavior, final size verification ensures accuracy
- **Log scrolling**: Manually scroll or check log after completion

---

## Contributing

Contributions are welcome! Please follow these guidelines:

1. **Code Style**: Follow C# coding conventions
2. **Documentation**: Update relevant .md files
3. **Testing**: Test with various directory sizes and scenarios
4. **Changelog**: Update this file with your changes

---

## License

MIT License - See project root for details

---

## Acknowledgments

- Microsoft for WinUI 3 and Windows App SDK
- CommunityToolkit team for MVVM library
- Contributors and testers

---

**Note**: This project is maintained and supported. Please report issues and suggest features!

