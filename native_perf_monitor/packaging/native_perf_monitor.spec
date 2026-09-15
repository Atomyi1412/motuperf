# -*- mode: python ; coding: utf-8 -*-

from pathlib import Path

from PyInstaller.utils.hooks import collect_data_files, collect_submodules


NATIVE_ROOT = Path(SPECPATH).parent
PROJECT_ROOT = NATIVE_ROOT.parent

datas = [
    (str(NATIVE_ROOT / "README.md"), "."),
]
binaries = []
hiddenimports = [
    "PyQt5.QtCore",
    "PyQt5.QtGui",
    "PyQt5.QtWidgets",
    "tidevice.__main__",
    "ios_device.main",
]

datas += collect_data_files("pyqtgraph", excludes=["**/examples/**", "**/tests/**"])
hiddenimports += collect_submodules("native_perf_monitor")
hiddenimports += collect_submodules("backend")
hiddenimports += collect_submodules("tidevice")
hiddenimports += collect_submodules("ios_device")
hiddenimports += collect_submodules(
    "pyqtgraph",
    filter=lambda name: all(
        blocked not in name.lower()
        for blocked in ("opengl", "examples", "jupyter", "test", "widgets.remotegraphicsview")
    ),
)

excludes = [
    "OpenGL",
    "PySide2",
    "PySide6",
    "PyQt6",
    "jupyter_rfb",
    "pytest",
    "scipy",
    "pandas",
    "torch",
    "numba",
    "llvmlite",
    "matplotlib",
]

a = Analysis(
    [str(NATIVE_ROOT / "launcher.py")],
    pathex=[str(PROJECT_ROOT), str(NATIVE_ROOT)],
    binaries=binaries,
    datas=datas,
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=excludes,
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="native-ios-perf-monitor",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name="native-ios-perf-monitor-portable",
)
