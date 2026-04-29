using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Point = System.Drawing.Point;

namespace LMCSHD
{
    public partial class MainWindow : INotifyPropertyChanged
    {
        // ---- Solid color ----
        private System.Windows.Media.Color _tpSolidColor = System.Windows.Media.Colors.White;
        public System.Windows.Media.Color TPSolidColor
        {
            get { return _tpSolidColor; }
            set
            {
                if (_tpSolidColor != value)
                {
                    _tpSolidColor = value;
                    OnPropertyChanged();
                }
            }
        }

        // ---- Walking pixel ----
        private DispatcherTimer _tpWalkTimer;
        private List<Point> _tpWalkSequence;
        private int _tpWalkIndex;

        private int _tpWalkSpeed = 30;
        public int TPWalkSpeed
        {
            get { return _tpWalkSpeed; }
            set
            {
                if (_tpWalkSpeed != value)
                {
                    _tpWalkSpeed = Math.Max(1, value);
                    if (_tpWalkTimer != null)
                        _tpWalkTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _tpWalkSpeed);
                    OnPropertyChanged();
                }
            }
        }

        private bool _tpWalkActive;
        public bool TPWalkActive
        {
            get { return _tpWalkActive; }
            set
            {
                if (_tpWalkActive != value)
                {
                    _tpWalkActive = value;
                    if (value) StartWalk(); else StopWalk();
                    OnPropertyChanged();
                }
            }
        }

        // ---- Per-section colors ----
        // Fixed palette: section i -> _tpSectionPalette[i % length].
        private static readonly Pixel[] _tpSectionPalette = new[]
        {
            new Pixel(255,   0,   0),  // red
            new Pixel(  0, 255,   0),  // green
            new Pixel(  0,   0, 255),  // blue
            new Pixel(255, 255,   0),  // yellow
            new Pixel(255,   0, 255),  // magenta
            new Pixel(  0, 255, 255),  // cyan
            new Pixel(255, 255, 255),  // white
            new Pixel(128, 128, 128),  // gray
        };

        private void TP_SolidFill_Click(object sender, RoutedEventArgs e)
        {
            var c = TPSolidColor;
            MatrixFrame.FillFrame(new Pixel(c.R, c.G, c.B));
            MatrixFrame.Refresh();
        }

        private void TP_Clear_Click(object sender, RoutedEventArgs e)
        {
            MatrixFrame.FillFrame(new Pixel(0, 0, 0));
            MatrixFrame.Refresh();
        }

        private void TP_PerSection_Click(object sender, RoutedEventArgs e)
        {
            MatrixFrame.FillFrame(new Pixel(0, 0, 0));
            for (int i = 0; i < MatrixFrame.Sections.Count; i++)
            {
                var s = MatrixFrame.Sections[i];
                var c = _tpSectionPalette[i % _tpSectionPalette.Length];
                for (int y = 0; y < s.Height; y++)
                    for (int x = 0; x < s.Width; x++)
                        MatrixFrame.Frame[(s.Y + y) * MatrixFrame.Width + (s.X + x)] = c;
            }
            MatrixFrame.Refresh();
        }

        private void StartWalk()
        {
            _tpWalkSequence = MatrixFrame.GetChainOrderCoords();
            _tpWalkIndex = 0;
            if (_tpWalkTimer == null)
            {
                _tpWalkTimer = new DispatcherTimer();
                _tpWalkTimer.Tick += TpWalkTick;
            }
            _tpWalkTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, _tpWalkSpeed));
            MatrixFrame.FillFrame(new Pixel(0, 0, 0));
            MatrixFrame.Refresh();
            _tpWalkTimer.Start();
        }

        private void StopWalk()
        {
            if (_tpWalkTimer != null) _tpWalkTimer.Stop();
        }

        private void TpWalkTick(object sender, EventArgs e)
        {
            if (_tpWalkSequence == null || _tpWalkSequence.Count == 0)
            {
                StopWalk();
                return;
            }
            // Erase previous lit pixel and draw current.
            int prev = (_tpWalkIndex == 0) ? _tpWalkSequence.Count - 1 : _tpWalkIndex - 1;
            var p = _tpWalkSequence[prev];
            var q = _tpWalkSequence[_tpWalkIndex];
            MatrixFrame.Frame[p.Y * MatrixFrame.Width + p.X] = new Pixel(0, 0, 0);
            MatrixFrame.Frame[q.Y * MatrixFrame.Width + q.X] = new Pixel(255, 255, 255);
            MatrixFrame.Refresh();
            _tpWalkIndex = (_tpWalkIndex + 1) % _tpWalkSequence.Count;
        }
    }
}
