VLC with Zoom demo
==================

This repository contains a simple demonstration of how to implement zoom functionality in a video player using VLC's libVLC library. The demo showcases how to zoom in and out of a video while maintaining smooth playback.    
Getting Started

A very doable WPF project, with suggested UX concept (main zoomed view + miniature with a yellow box) is solid for precise navigation.
Below is a practical, performant way to implement it in WPF with LibVLCSharp using a custom video renderer. This avoids the fragility of duplicating pixels from a hardware-accelerated window (BitBlt/DWM/PrintWindow will often return black or stutter with D3D surfaces) while still preserving your core idea: the visible views don’t directly host a VLC widget; instead, a hidden player feeds a pixel buffer that you render where and how you want.
If you absolutely want the offscreen window + low-level duplication path, I (CoPilot AI) include that as an alternative further below—with clear caveats and a code sketch.

## High‑level architecture 
### Core concepts

- Single media pipeline (LibVLC media player) pushes frames into your buffer via callbacks.
- You expose that buffer to WPF as a WriteableBitmap (“full frame”).
- Main (left) shows a cropped view of that full frame (zoomed/panned).
- Miniature (right) shows the full frame, with a yellow overlay rectangle indicating the crop.
- Mouse wheel zooms (centered on the mouse position); drag pans. Zoom % is shown in the bottom control strip.

#### Why this over BitBlt/DWM duplication?

Modern VLC uses Direct3D surfaces when possible. GDI BitBlt/PrintWindow often can’t read back GPU textures reliably in real time (you get black, tearing, or heavy CPU/GPU stalls).
LibVLC’s custom video callbacks give you stable frames in a pixel format you pick (e.g., RV32/BGRA), excellent for WPF.


#### When/if you revisit GPU later (only when you feel like it)
A gentler route than raw D3D9Ex interop is: Keep the CPU path for now.
When you decide to try GPU again, we can switch to Microsoft’s WPF DirectX Extensions (D3D11Image) (no D3D9 bridge). It still requires some setup, but it’s cleaner than the shared-handle hop. I can prep a minimal sample for you when the time comes.


### Meanwhile—polish ideas (easy wins)

- Perf during pan: set RenderOptions.BitmapScalingMode="LowQuality" while dragging; switch back to HighQuality on mouse-up. It makes panning feel smoother.
- Frame drop on busy UI: you already have TryLock(Duration.Zero)—that’s perfect. Dropping a frame under lock contention keeps things responsive.
- Miniature UX: double‑click to center; right‑drag to set a new crop (I can add these quickly).
- Cursor assets: if you share your open/closed hand .cur, I’ll wire them precisely so hover=“open” and drag=“closed”.