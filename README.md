# CH4 Torque Monitor

Qt 5 desktop frontend for read-only CH4 torque monitoring over RS-485 / Modbus RTU. The Qt application is under `qt/`. The original verified C# implementation and its test records remain under `source/` and in the Chinese notes for reference.

## Features

- Qt 5 Widgets interface with live trend, serial selection, protocol settings, and demo mode.
- Modbus RTU function `03` only. The application does not write registers or issue device zero/calibration commands.
- Configurable station, CH4 register address, data type, byte order, baud rate, and polling interval.
- Optional CSV recording. Rows identify live samples as uncalibrated raw values.
- Settings are stored by `QSettings` in the current user's application settings, not in the repository.

The default address/type/order are the candidate CH4 values previously checked against one device. Verify these against the actual transmitter. Raw counts are not calibrated torque; use known torque and manufacturer documentation before interpreting them as N·m. The included device notes describe the scope and limits of the earlier read-only verification.

## Build on Windows

Install Qt 5.15.2 with the MSVC 2019 64-bit kit and Qt Serial Port, then open `qt/CH4Torque.pro` in Qt Creator or run from a Visual Studio developer prompt:

```powershell
qmake qt\CH4Torque.pro
nmake
```

GitHub Actions builds the Windows release package when a `v*` tag is pushed. The build uses Qt 5.15.2 MSVC 2019 and deploys required Qt runtime files alongside the application.

## Prior verification

`软件测试结果.txt` records 42 passing tests for the original C# protocol implementation. `实机首次只读查询.txt` records the earlier device checks. Neither record claims calibration or measurement accuracy. No physical device is accessed by the automated tests.
