@echo off
rem ---------------------------------------------------------------------------
rem  Launches DeskSprite with the BitBlt swapchain model.
rem
rem  Why: DXGI flip model swapchains do NOT support transparency via
rem  DwmExtendFrameIntoClientArea (Unity's official docs say so explicitly).
rem  A transparent / overlay window MUST use the BitBlt model.
rem
rem  This is only a quick test. The permanent fix is a Player Setting:
rem    Edit > Project Settings > Player > Resolution and Presentation
rem    uncheck "Use DXGI Flip Model Swapchain for D3D11"
rem
rem  If you rebuild to a different folder, update the path below.
rem ---------------------------------------------------------------------------

start "" "D:\DeskSpriteTest\DeskSprite.exe" -force-d3d11-bitblt-model
