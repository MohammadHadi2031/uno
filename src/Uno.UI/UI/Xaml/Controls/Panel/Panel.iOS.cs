using System;
using Uno.UI.Views.Controls;
using Windows.UI.Xaml;
using Uno.Extensions;
using Uno.Logging;
using Uno.Disposables;
using Windows.UI.Xaml.Controls;
using Uno.UI.Controls;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Xaml.Media;

using UIKit;
using CoreGraphics;

namespace Windows.UI.Xaml.Controls
{
	public partial class Panel
	{
		private readonly BorderLayerRenderer _borderRenderer = new BorderLayerRenderer();

		public Panel()
		{
			Initialize();

			this.RegisterLoadActions(() => UpdateBackground(), () => _borderRenderer.Clear());
		}

		partial void Initialize();

		public override void SubviewAdded(UIView uiview)
		{
			base.SubviewAdded(uiview);

			var element = uiview as IFrameworkElement;
			if (element != null)
			{
				OnChildAdded(element);
			}
		}

		partial void OnBorderBrushChangedPartial(Brush oldValue, Brush newValue)
		{
			UpdateBackground();
		}

		partial void OnBorderThicknessChangedPartial(Thickness oldValue, Thickness newValue)
		{
			SetNeedsLayout();
			UpdateBackground();
		}

		partial void OnPaddingChangedPartial(Thickness oldValue, Thickness newValue)
		{
			SetNeedsLayout();
		}

		protected override void OnBackgroundChanged(DependencyPropertyChangedEventArgs args)
		{
			// Ignore the background changes provided from base, we're rendering it using the CALayer.
			// base.OnBackgroundChanged(e);

			var old = args.OldValue as ImageBrush;
			if (old != null)
			{
				old.ImageChanged -= OnBackgroundImageBrushChanged;
			}
			var imgBrush = args.NewValue as ImageBrush;
			if (imgBrush != null)
			{
				imgBrush.ImageChanged += OnBackgroundImageBrushChanged;
			}

			UpdateBackground();
		}

		private void OnBackgroundImageBrushChanged(UIImage backgroundImage)
		{
			UpdateBackground(backgroundImage);
		}

		partial void OnCornerRadiusChangedPartial(CornerRadius oldValue, CornerRadius newValue)
		{
			UpdateBackground();
		}

		private void UpdateBackground(UIImage backgroundImage = null)
		{
			// Checking for Window avoids re-creating the layer until it is actually used.
			if (IsLoaded)
			{
				if (backgroundImage == null)
				{
					(Background as ImageBrush)?.ImageSource?.TryOpenSync(out backgroundImage);
				}

				_borderRenderer.UpdateLayer(
					this,
					Background,
					InternalBackgroundSizing,
					BorderThickness,
					BorderBrush,
					CornerRadius,
					backgroundImage
				);
			}
		}

		partial void UpdateBorder()
		{
			UpdateBackground();
		}

		protected virtual void OnChildrenChanged()
		{
			SetNeedsLayout();
		}

		protected override void OnAfterArrange()
		{
			base.OnAfterArrange();

			//We trigger all layoutUpdated animations
			_transitionHelper?.LayoutUpdatedTransition();
		}

		protected override void OnBeforeArrange()
		{
			base.OnBeforeArrange();

			//We set childrens position for the animations before the arrange
			_transitionHelper?.SetInitialChildrenPositions();

			UpdateBackground();
		}

		/// <summary>        
		/// Support for the C# collection initializer style.
		/// Allows items to be added like this 
		/// new Panel 
		/// {
		///    new Border()
		/// }
		/// </summary>
		/// <param name="view"></param>
		public new void Add(UIView view)
		{
			Children.Add(view);
		}

		public bool HitTestOutsideFrame
		{
			get;
			set;
		}

		public override UIView HitTest(CGPoint point, UIEvent uievent)
		{
			// All touches that are on this view (and not its subviews) are ignored
			return HitTestOutsideFrame ? this.HitTestOutsideFrame(point, uievent) : base.HitTest(point, uievent);
		}

		bool ICustomClippingElement.AllowClippingToLayoutSlot => CornerRadius == CornerRadius.None;
		bool ICustomClippingElement.ForceClippingToLayoutSlot => false;
	}
}
