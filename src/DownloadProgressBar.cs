using System.Drawing;
using System.Windows.Forms;

namespace JtdxAutoCq
{
    sealed class DownloadProgressBar : ProgressBar
    {
        public DownloadProgressBar()
        { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        public new int Value
        { get { return base.Value; } set { base.Value = value; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle track = ClientRectangle;
            e.Graphics.Clear(Color.FromArgb(226, 235, 235));
            int width = track.Width * (Value - Minimum) / (Maximum - Minimum);
            using (var fill = new SolidBrush(Color.FromArgb(0, 100, 91)))
                e.Graphics.FillRectangle(fill, 0, 0, width, track.Height);
            ControlPaint.DrawBorder(e.Graphics, track, Color.FromArgb(185, 203, 208), ButtonBorderStyle.Solid);
            if (Value > Minimum)
            {
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
                TextRenderer.DrawText(e.Graphics, Value + "%", Font, track, Color.FromArgb(21, 38, 58), flags);
                var saved = e.Graphics.Save();
                e.Graphics.SetClip(new Rectangle(0, 0, width, track.Height));
                TextRenderer.DrawText(e.Graphics, Value + "%", Font, track, Color.White, flags);
                e.Graphics.Restore(saved);
            }
        }
    }
}
