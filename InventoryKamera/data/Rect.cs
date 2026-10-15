using System.Drawing;
using System.Runtime.InteropServices;

namespace InventoryKamera
{
	[StructLayout(LayoutKind.Sequential)]
	public struct Rect
	{
		public Rect(Rect Rectangle) : this(Rectangle.Left, Rectangle.Top, Rectangle.Right, Rectangle.Bottom)
		{
		}

		public Rect(int Left, int Top, int Right, int Bottom)
		{
			this.Left = Left;
			this.Top = Top;
			this.Right = Right;
			this.Bottom = Bottom;
		}

		// The auto-property backing fields keep the sequential Win32 RECT
		// layout (left, top, right, bottom) used by the P/Invoke callers.
		public int Left { get; set; }

		public int Top { get; set; }

		public int Right { get; set; }

		public int Bottom { get; set; }

		public int X
		{
			get => Left;
			set => Left = value;
		}

		public int Y
		{
			get => Top;
			set => Top = value;
		}

		public int Height
		{
			get => Bottom - Top;
			set => Bottom = value + Top;
		}

		public int Width
		{
			get => Right - Left;
			set => Right = value + Left;
		}

		public Point Location
		{
			get => new Point(Left, Top);
			set
			{
				Left = value.X;
				Top = value.Y;
			}
		}

		public Size Size
		{
			get => new Size(Width, Height);
			set
			{
				Right = value.Width + Left;
				Bottom = value.Height + Top;
			}
		}

		public static implicit operator Rectangle(Rect Rectangle) => new Rectangle(Rectangle.Left, Rectangle.Top, Rectangle.Width, Rectangle.Height);

		public static implicit operator Rect(Rectangle Rectangle) => new Rect(Rectangle.Left, Rectangle.Top, Rectangle.Right, Rectangle.Bottom);

		public static bool operator ==(Rect Rectangle1, Rect Rectangle2) => Rectangle1.Equals(Rectangle2);

		public static bool operator !=(Rect Rectangle1, Rect Rectangle2) => !Rectangle1.Equals(Rectangle2);

		public override string ToString()
		{
			return "{Left: " + Left + "; " + "Top: " + Top + "; Right: " + Right + "; Bottom: " + Bottom + "}";
		}

		public override int GetHashCode()
		{
			return ToString().GetHashCode();
		}

		public bool Equals(Rect Rectangle)
		{
			return Rectangle.Left == Left && Rectangle.Top == Top && Rectangle.Right == Right && Rectangle.Bottom == Bottom;
		}

		public override bool Equals(object obj)
		{
			switch (obj)
			{
				case Rect rect:
					return Equals(rect);

				case Rectangle rectangle:
					return Equals(new Rect(rectangle));
			}

			return false;
		}
	}
}
