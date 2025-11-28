using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace VideoZoom;

public class ZoomState : ObservableObject
{
    private string _currentFile="";
    private double _zoom = 1.0;            // magnification
    private double _centerX = 0;           // in source pixels
    private double _centerY = 0;
    private int _videoW, _videoH;

    public int VideoW { get => _videoW; set => SetProperty(ref _videoW, value); }
    public int VideoH { get => _videoH; set => SetProperty(ref _videoH, value); }

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetProperty(ref _zoom, Math.Clamp(value, 1.0, 20.0)))
                OnPropertyChanged(nameof(ZoomText));
        }
    }

    public string ZoomText => $"Zoom: {Zoom * 100:0}%";


    public string CurrentOpenedFile
    {
        get => _currentFile;
        set
        {
            _currentFile = value;
            OnPropertyChanged(nameof(CurrentOpenedFilePath));
        }
    }
    public string CurrentOpenedFilePath => $"{_currentFile}";

    public double CenterX { get => _centerX; set => SetProperty(ref _centerX, value); }
    public double CenterY { get => _centerY; set => SetProperty(ref _centerY, value); }

    public Int32Rect GetCropRect()
    {
        if (VideoW == 0 || VideoH == 0) return new Int32Rect(0, 0, 0, 0);

        double cropW = VideoW / Zoom;
        double cropH = VideoH / Zoom;

        double x = CenterX - cropW / 2;
        double y = CenterY - cropH / 2;

        // clamp to bounds
        x = Math.Clamp(x, 0, Math.Max(0, VideoW - cropW));
        y = Math.Clamp(y, 0, Math.Max(0, VideoH - cropH));

        return new Int32Rect((int)Math.Round(x), (int)Math.Round(y),
                             (int)Math.Round(cropW), (int)Math.Round(cropH));
    }

    public void ZoomAtPoint(double factor, double mouseXSrc, double mouseYSrc)
    {
        // mouseXSrc/mouseYSrc in source pixels (we’ll compute these from UI mouse pos)
        double newZoom = Math.Clamp(Zoom * factor, 1.0, 20.0);

        // Keep the mouse position stable relative to crop when zooming
        double cropWOld = VideoW / Zoom;
        double cropHOld = VideoH / Zoom;
        double cropWNew = VideoW / newZoom;
        double cropHNew = VideoH / newZoom;

        // find relative offset in the old crop and apply to new
        var rectOld = GetCropRect();
        double relX = (mouseXSrc - rectOld.X) / cropWOld;
        double relY = (mouseYSrc - rectOld.Y) / cropHOld;

        double newX = mouseXSrc - relX * cropWNew;
        double newY = mouseYSrc - relY * cropHNew;

        CenterX = newX + cropWNew / 2;
        CenterY = newY + cropHNew / 2;
        Zoom = newZoom;
    }

    public void PanBy(double deltaSrcX, double deltaSrcY)
    {
        CenterX = Math.Clamp(CenterX + deltaSrcX, 0, VideoW);
        CenterY = Math.Clamp(CenterY + deltaSrcY, 0, VideoH);
    }
}