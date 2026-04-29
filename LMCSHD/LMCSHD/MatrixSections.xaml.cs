using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LMCSHD
{
    public partial class MatrixSections : Window
    {
        public ObservableCollection<SectionVm> SectionList { get; set; }

        // Exposed as static so DataGridComboBoxColumn can bind via x:Static.
        public static Array OrientationValues { get; } = Enum.GetValues(typeof(PixelOrder.Orientation));
        public static Array StartCornerValues { get; } = Enum.GetValues(typeof(PixelOrder.StartCorner));
        public static Array NewLineValues    { get; } = Enum.GetValues(typeof(PixelOrder.NewLine));

        public MatrixSections()
        {
            SectionList = new ObservableCollection<SectionVm>(
                MatrixFrame.Sections.Select(SectionVm.FromSection)
            );
            DataContext = this;
            InitializeComponent();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            SectionList.Add(new SectionVm
            {
                X = 0,
                Y = 0,
                Width = MatrixFrame.Width,
                Height = MatrixFrame.Height,
                Orientation = PixelOrder.Orientation.HZ,
                StartCorner = PixelOrder.StartCorner.TL,
                NewLine = PixelOrder.NewLine.SC,
            });
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            var selected = SectionsGrid.SelectedItem as SectionVm;
            if (selected != null) SectionList.Remove(selected);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            SectionList.Clear();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            // Force any in-progress cell edits to commit before we read SectionList.
            SectionsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            MatrixFrame.Sections = SectionList.Select(vm => vm.ToSection()).ToList();
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }

    // DataGrid binds against a class (Section is a struct, won't notify on edit).
    public class SectionVm
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public PixelOrder.Orientation Orientation { get; set; }
        public PixelOrder.StartCorner StartCorner { get; set; }
        public PixelOrder.NewLine NewLine { get; set; }

        public Section ToSection()
        {
            return new Section(X, Y, Width, Height, Orientation, StartCorner, NewLine);
        }

        public static SectionVm FromSection(Section s)
        {
            return new SectionVm
            {
                X = s.X,
                Y = s.Y,
                Width = s.Width,
                Height = s.Height,
                Orientation = s.Orientation,
                StartCorner = s.StartCorner,
                NewLine = s.NewLine,
            };
        }
    }
}
