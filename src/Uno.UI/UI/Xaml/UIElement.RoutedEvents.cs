﻿// #define TRACE_ROUTED_EVENT_BUBBLING

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Uno;
using Uno.Extensions;
using Uno.Logging;
using Uno.UI;
using Uno.UI.Core;
using Uno.UI.Extensions;
using Uno.UI.Xaml;
using Uno.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI.Xaml.Input;

#if __IOS__
using UIKit;
#endif

namespace Windows.UI.Xaml
{
	/*
		This partial file handles the registration and bubbling of routed events of a UIElement
		
		The API exposed by this file to its native parts are:
			partial void AddPointerHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
			partial void AddGestureHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
			partial void AddKeyHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
			partial void AddFocusHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
			partial void RemovePointerHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
			partial void RemoveGestureHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
			partial void RemoveKeyHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
			partial void RemoveFocusHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
			internal bool RaiseEvent(RoutedEvent routedEvent, RoutedEventArgs args);

		The native components are responsible to subscribe to the native events, interpret them,
		and then raise the recognized events using the "RaiseEvent" API.

		Here the state machine of the bubbling logic:

	[1]---------------------+
	| An event is fired     |
	+--------+--------------+
	         |
	[2]------v--------------+
	| Event is dispatched   |
	| to corresponding      |                    [12]
	| element               |                      ^
	+-------yes-------------+                      |
	         |                             [11]---no--------------+
	         |<---[13]-raise on parent---yes A parent is          |
	         |                             | defined?             |
	[3]------v--------------+              |                      |
	| Any local handlers?   no--------+    +-------^--------------+
	+-------yes-------------+         |            |
	         |                        |    [10]----+--------------+
	[4]------v--------------+         |    | Event is bubbling    |
	| Invoke local handlers |         |    | to parent in         <--+
	+--------+--------------+         |    | managed code (Uno)   |  |
	         |                        |    +-------^--------------+  |
	[5]------v--------------+         |            |                 |
	| Is the event handled  |         v    [6]-----no-------------+  |
	| by local handlers?    no------------>| Event is coming from |  |
	+-------yes-------------+              | platform?            |  |
	         |                             +------yes-------------+  |
	[9]------v--------------+                      |                 |
	| Any parent interested |              [7]-----v--------------+  |
	| by this event?        yes-+          | Is the event         |  |
	+-------no--------------+   |          | bubbling natively?   no-+
	         |                  |          +------yes-------------+
	[12]-----v--------------+   |                  |
	| Processing finished   |   v          [8]-----v--------------+
	| for this event.       |  [10]        | Event is returned    |
	+-----------------------+              | for native           |
	                                       | bubbling in platform |
	                                       +----------------------+

	Note: this class is handling all this flow except [1] and [2]. */

	partial class UIElement
	{
		public static RoutedEvent PointerPressedEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerPressed);

		public static RoutedEvent PointerReleasedEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerReleased);

		public static RoutedEvent PointerEnteredEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerEntered);

		public static RoutedEvent PointerExitedEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerExited);

		public static RoutedEvent PointerMovedEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerMoved);

		public static RoutedEvent PointerCanceledEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerCanceled);

		public static RoutedEvent PointerCaptureLostEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerCaptureLost);

#if !__WASM__
		[global::Uno.NotImplemented]
#endif
		public static RoutedEvent PointerWheelChangedEvent { get; } = new RoutedEvent(RoutedEventFlag.PointerWheelChanged);

		public static RoutedEvent ManipulationStartingEvent { get; } = new RoutedEvent(RoutedEventFlag.ManipulationStarting);

		public static RoutedEvent ManipulationStartedEvent { get; } = new RoutedEvent(RoutedEventFlag.ManipulationStarted);

		public static RoutedEvent ManipulationDeltaEvent { get; } = new RoutedEvent(RoutedEventFlag.ManipulationDelta);

		public static RoutedEvent ManipulationInertiaStartingEvent { get; } = new RoutedEvent(RoutedEventFlag.ManipulationInertiaStarting);

		public static RoutedEvent ManipulationCompletedEvent { get; } = new RoutedEvent(RoutedEventFlag.ManipulationCompleted);

		public static RoutedEvent TappedEvent { get; } = new RoutedEvent(RoutedEventFlag.Tapped);

		public static RoutedEvent DoubleTappedEvent { get; } = new RoutedEvent(RoutedEventFlag.DoubleTapped);

		public static RoutedEvent RightTappedEvent { get; } = new RoutedEvent(RoutedEventFlag.RightTapped);

		public static RoutedEvent HoldingEvent { get; } = new RoutedEvent(RoutedEventFlag.Holding);

		/* ** */
		internal /* ** */ static RoutedEvent DragStartingEvent { get; } = new RoutedEvent(RoutedEventFlag.DragStarting);

		public static RoutedEvent DragEnterEvent { get; } = new RoutedEvent(RoutedEventFlag.DragEnter);

		public static RoutedEvent DragOverEvent { get; } = new RoutedEvent(RoutedEventFlag.DragOver);

		public static RoutedEvent DragLeaveEvent { get; } = new RoutedEvent(RoutedEventFlag.DragLeave);

		public static RoutedEvent DropEvent { get; } = new RoutedEvent(RoutedEventFlag.Drop);

		/* ** */
		internal /* ** */  static RoutedEvent DropCompletedEvent { get; } = new RoutedEvent(RoutedEventFlag.DropCompleted);

		public static RoutedEvent KeyDownEvent { get; } = new RoutedEvent(RoutedEventFlag.KeyDown);

		public static RoutedEvent KeyUpEvent { get; } = new RoutedEvent(RoutedEventFlag.KeyUp);

		internal static RoutedEvent GotFocusEvent { get; } = new RoutedEvent(RoutedEventFlag.GotFocus);

		internal static RoutedEvent LostFocusEvent { get; } = new RoutedEvent(RoutedEventFlag.LostFocus);

		public static RoutedEvent GettingFocusEvent { get; } = new RoutedEvent(RoutedEventFlag.GettingFocus);

		public static RoutedEvent LosingFocusEvent { get; } = new RoutedEvent(RoutedEventFlag.LosingFocus);

		public static RoutedEvent NoFocusCandidateFoundEvent { get; } = new RoutedEvent(RoutedEventFlag.NoFocusCandidateFound);

		private struct RoutedEventHandlerInfo
		{
			internal RoutedEventHandlerInfo(object handler, bool handledEventsToo)
			{
				Handler = handler;
				HandledEventsToo = handledEventsToo;
			}

			internal object Handler { get; }

			internal bool HandledEventsToo { get; }
		}

		#region EventsBubblingInManagedCode DependencyProperty

		public static DependencyProperty EventsBubblingInManagedCodeProperty { get; } = DependencyProperty.Register(
			"EventsBubblingInManagedCode",
			typeof(RoutedEventFlag),
			typeof(UIElement),
			new FrameworkPropertyMetadata(
				RoutedEventFlag.None,
				FrameworkPropertyMetadataOptions.Inherits)
			{
				CoerceValueCallback = CoerceRoutedEventFlag
			}
		);

		public RoutedEventFlag EventsBubblingInManagedCode
		{
			get => (RoutedEventFlag)GetValue(EventsBubblingInManagedCodeProperty);
			set => SetValue(EventsBubblingInManagedCodeProperty, value);
		}

		#endregion

		#region SubscribedToHandledEventsToo DependencyProperty

		private static DependencyProperty SubscribedToHandledEventsTooProperty { get; } =
			DependencyProperty.Register(
				"SubscribedToHandledEventsToo",
				typeof(RoutedEventFlag),
				typeof(UIElement),
				new FrameworkPropertyMetadata(
					RoutedEventFlag.None,
					FrameworkPropertyMetadataOptions.Inherits)
				{
					CoerceValueCallback = CoerceRoutedEventFlag
				}
			);

		private RoutedEventFlag SubscribedToHandledEventsToo
		{
			get => (RoutedEventFlag)GetValue(SubscribedToHandledEventsTooProperty);
			set => SetValue(SubscribedToHandledEventsTooProperty, value);
		}

		#endregion

		private static object CoerceRoutedEventFlag(DependencyObject dependencyObject, object baseValue)
		{
			// This is a Coerce method for both EventsBubblingInManagedCodeProperty and SubscribedToHandledEventsTooProperty

			var @this = (UIElement)dependencyObject;

			var localValue = @this.GetPrecedenceSpecificValue(
				SubscribedToHandledEventsTooProperty,
				DependencyPropertyValuePrecedences.Local); // should be the same than localValue on first assignment

			if (!(localValue is RoutedEventFlag local))
			{
				return baseValue; // local not set, no coerced value to set
			}

			var inheritedValue = @this.GetPrecedenceSpecificValue(
				SubscribedToHandledEventsTooProperty,
				DependencyPropertyValuePrecedences.Inheritance);

			if (inheritedValue is RoutedEventFlag inherited)
			{
				return local | inherited; // coerced value is a merge between local and inherited
			}

			return baseValue; // no inherited value, nothing to do
		}

		private readonly Dictionary<RoutedEvent, List<RoutedEventHandlerInfo>> _eventHandlerStore
			= new Dictionary<RoutedEvent, List<RoutedEventHandlerInfo>>();

		public event RoutedEventHandler LostFocus
		{
			add => AddHandler(LostFocusEvent, value, false);
			remove => RemoveHandler(LostFocusEvent, value);
		}

		public event RoutedEventHandler GotFocus
		{
			add => AddHandler(GotFocusEvent, value, false);
			remove => RemoveHandler(GotFocusEvent, value);
		}

		public event TypedEventHandler<UIElement, LosingFocusEventArgs> LosingFocus
		{
			add => AddHandler(LosingFocusEvent, value, false);
			remove => RemoveHandler(LosingFocusEvent, value);
		}

		public event TypedEventHandler<UIElement, GettingFocusEventArgs> GettingFocus
		{
			add => AddHandler(GettingFocusEvent, value, false);
			remove => RemoveHandler(GettingFocusEvent, value);
		}

		public event TypedEventHandler<UIElement, NoFocusCandidateFoundEventArgs> NoFocusCandidateFound
		{
			add => AddHandler(NoFocusCandidateFoundEvent, value, false);
			remove => RemoveHandler(NoFocusCandidateFoundEvent, value);
		}

		public event PointerEventHandler PointerCanceled
		{
			add => AddHandler(PointerCanceledEvent, value, false);
			remove => RemoveHandler(PointerCanceledEvent, value);
		}

		public event PointerEventHandler PointerCaptureLost
		{
			add => AddHandler(PointerCaptureLostEvent, value, false);
			remove => RemoveHandler(PointerCaptureLostEvent, value);
		}

		public event PointerEventHandler PointerEntered
		{
			add => AddHandler(PointerEnteredEvent, value, false);
			remove => RemoveHandler(PointerEnteredEvent, value);
		}

		public event PointerEventHandler PointerExited
		{
			add => AddHandler(PointerExitedEvent, value, false);
			remove => RemoveHandler(PointerExitedEvent, value);
		}

		public event PointerEventHandler PointerMoved
		{
			add => AddHandler(PointerMovedEvent, value, false);
			remove => RemoveHandler(PointerMovedEvent, value);
		}

		public event PointerEventHandler PointerPressed
		{
			add => AddHandler(PointerPressedEvent, value, false);
			remove => RemoveHandler(PointerPressedEvent, value);
		}

		public event PointerEventHandler PointerReleased
		{
			add => AddHandler(PointerReleasedEvent, value, false);
			remove => RemoveHandler(PointerReleasedEvent, value);
		}

#if !__WASM__ && !__SKIA__
		[global::Uno.NotImplemented]
#endif
		public event PointerEventHandler PointerWheelChanged
		{
			add => AddHandler(PointerWheelChangedEvent, value, false);
			remove => RemoveHandler(PointerWheelChangedEvent, value);
		}

		public event ManipulationStartingEventHandler ManipulationStarting
		{
			add => AddHandler(ManipulationStartingEvent, value, false);
			remove => RemoveHandler(ManipulationStartingEvent, value);
		}

		public event ManipulationStartedEventHandler ManipulationStarted
		{
			add => AddHandler(ManipulationStartedEvent, value, false);
			remove => RemoveHandler(ManipulationStartedEvent, value);
		}

		public event ManipulationDeltaEventHandler ManipulationDelta
		{
			add => AddHandler(ManipulationDeltaEvent, value, false);
			remove => RemoveHandler(ManipulationDeltaEvent, value);
		}

		public event ManipulationInertiaStartingEventHandler ManipulationInertiaStarting
		{
			add => AddHandler(ManipulationInertiaStartingEvent, value, false);
			remove => RemoveHandler(ManipulationInertiaStartingEvent, value);
		}

		public event ManipulationCompletedEventHandler ManipulationCompleted
		{
			add => AddHandler(ManipulationCompletedEvent, value, false);
			remove => RemoveHandler(ManipulationCompletedEvent, value);
		}

		public event TappedEventHandler Tapped
		{
			add => AddHandler(TappedEvent, value, false);
			remove => RemoveHandler(TappedEvent, value);
		}

		public event DoubleTappedEventHandler DoubleTapped
		{
			add => AddHandler(DoubleTappedEvent, value, false);
			remove => RemoveHandler(DoubleTappedEvent, value);
		}

		public event RightTappedEventHandler RightTapped
		{
			add => AddHandler(RightTappedEvent, value, false);
			remove => RemoveHandler(RightTappedEvent, value);
		}

		public event HoldingEventHandler Holding
		{
			add => AddHandler(HoldingEvent, value, false);
			remove => RemoveHandler(HoldingEvent, value);
		}

		public event TypedEventHandler<UIElement, DragStartingEventArgs> DragStarting
		{
			add => AddHandler(DragStartingEvent, value, false);
			remove => RemoveHandler(DragStartingEvent, value);
		}

		public event DragEventHandler DragEnter
		{
			add => AddHandler(DragEnterEvent, value, false);
			remove => RemoveHandler(DragEnterEvent, value);
		}

		public event DragEventHandler DragLeave
		{
			add => AddHandler(DragLeaveEvent, value, false);
			remove => RemoveHandler(DragLeaveEvent, value);
		}

		public event DragEventHandler DragOver
		{
			add => AddHandler(DragOverEvent, value, false);
			remove => RemoveHandler(DragOverEvent, value);
		}

		public event DragEventHandler Drop
		{
			add => AddHandler(DropEvent, value, false);
			remove => RemoveHandler(DropEvent, value);
		}

		public event TypedEventHandler<UIElement, DropCompletedEventArgs> DropCompleted
		{
			add => AddHandler(DropCompletedEvent, value, false);
			remove => RemoveHandler(DropCompletedEvent, value);
		}

#if __MACOS__
		public new event KeyEventHandler KeyDown
#else
		public event KeyEventHandler KeyDown
#endif
		{
			add => AddHandler(KeyDownEvent, value, false);
			remove => RemoveHandler(KeyDownEvent, value);
		}

#if __MACOS__
		public new event KeyEventHandler KeyUp
#else
		public event KeyEventHandler KeyUp
#endif
		{
			add => AddHandler(KeyUpEvent, value, false);
			remove => RemoveHandler(KeyUpEvent, value);
		}

		/// <summary>
		/// Inserts an event handler as the first event handler.
		/// This is for internal use only and allow controls to lazily subscribe to event only when required while remaining the first invoked handler,
		/// which is ** really ** important when marking an event as handled.
		/// </summary>
		private protected void InsertHandler(RoutedEvent routedEvent, object handler, bool handledEventsToo = false)
		{
			var handlers = _eventHandlerStore.FindOrCreate(routedEvent, () => new List<RoutedEventHandlerInfo>());
			if (handlers.Count > 0)
			{
				handlers.Insert(0, new RoutedEventHandlerInfo(handler, handledEventsToo));
			}
			else
			{
				handlers.Add(new RoutedEventHandlerInfo(handler, handledEventsToo));
			}

			AddHandler(routedEvent, handlers.Count, handler, handledEventsToo);

			if (handledEventsToo
				&& !routedEvent.IsAlwaysBubbled) // This event is always bubbled, no needs to update the flag
			{
				UpdateSubscribedToHandledEventsToo();
			}
		}

		public void AddHandler(RoutedEvent routedEvent, object handler, bool handledEventsToo)
		{
			var handlers = _eventHandlerStore.FindOrCreate(routedEvent, () => new List<RoutedEventHandlerInfo>());
			handlers.Add(new RoutedEventHandlerInfo(handler, handledEventsToo));

			AddHandler(routedEvent, handlers.Count, handler, handledEventsToo);

			if (handledEventsToo
				&& !routedEvent.IsAlwaysBubbled) // This event is always bubbled, no needs to update the flag
			{
				UpdateSubscribedToHandledEventsToo();
			}
		}

		private void AddHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo)
		{
			if (routedEvent.IsPointerEvent)
			{
				AddPointerHandler(routedEvent, handlersCount, handler, handledEventsToo);
			}
			else if (routedEvent.IsKeyEvent)
			{
				AddKeyHandler(routedEvent, handlersCount, handler, handledEventsToo);
			}
			else if (routedEvent.IsFocusEvent)
			{
				AddFocusHandler(routedEvent, handlersCount, handler, handledEventsToo);
			}
			else if (routedEvent.IsManipulationEvent)
			{
				AddManipulationHandler(routedEvent, handlersCount, handler, handledEventsToo);
			}
			else if (routedEvent.IsGestureEvent)
			{
				AddGestureHandler(routedEvent, handlersCount, handler, handledEventsToo);
			}
			else if (routedEvent.IsDragAndDropEvent)
			{
				AddDragAndDropHandler(routedEvent, handlersCount, handler, handledEventsToo);
			}
		}

		partial void AddPointerHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
		partial void AddKeyHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
		partial void AddFocusHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
		partial void AddManipulationHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
		partial void AddGestureHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);
		partial void AddDragAndDropHandler(RoutedEvent routedEvent, int handlersCount, object handler, bool handledEventsToo);

		public void RemoveHandler(RoutedEvent routedEvent, object handler)
		{
			if (_eventHandlerStore.TryGetValue(routedEvent, out var handlers))
			{
				var matchingHandler = handlers.FirstOrDefault(handlerInfo => (handlerInfo.Handler as Delegate).Equals(handler as Delegate));

				if (!matchingHandler.Equals(default(RoutedEventHandlerInfo)))
				{
					handlers.Remove(matchingHandler);

					if (matchingHandler.HandledEventsToo
						&& !routedEvent.IsAlwaysBubbled) // This event is always bubbled, no need to update the flag
					{
						UpdateSubscribedToHandledEventsToo();
					}
				}

				RemoveHandler(routedEvent, handlers.Count, handler);
			}
			else
			{
				RemoveHandler(routedEvent, remainingHandlersCount: -1, handler);
			}
		}

		private void RemoveHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler)
		{
			if (routedEvent.IsPointerEvent)
			{
				RemovePointerHandler(routedEvent, remainingHandlersCount, handler);
			}
			else if (routedEvent.IsKeyEvent)
			{
				RemoveKeyHandler(routedEvent, remainingHandlersCount, handler);
			}
			else if (routedEvent.IsFocusEvent)
			{
				RemoveFocusHandler(routedEvent, remainingHandlersCount, handler);
			}
			else if (routedEvent.IsManipulationEvent)
			{
				RemoveManipulationHandler(routedEvent, remainingHandlersCount, handler);
			}
			else if (routedEvent.IsGestureEvent)
			{
				RemoveGestureHandler(routedEvent, remainingHandlersCount, handler);
			}
			else if (routedEvent.IsDragAndDropEvent)
			{
				RemoveDragAndDropHandler(routedEvent, remainingHandlersCount, handler);
			}
		}

		partial void RemovePointerHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
		partial void RemoveKeyHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
		partial void RemoveFocusHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
		partial void RemoveManipulationHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
		partial void RemoveGestureHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);
		partial void RemoveDragAndDropHandler(RoutedEvent routedEvent, int remainingHandlersCount, object handler);

		private int CountHandler(RoutedEvent routedEvent)
			=> _eventHandlerStore.TryGetValue(routedEvent, out var handlers)
				? handlers.Count
				: 0;

		private void UpdateSubscribedToHandledEventsToo()
		{
			var subscribedToHandledEventsToo = RoutedEventFlag.None;

			foreach (var eventHandlers in _eventHandlerStore)
			{
				if (eventHandlers.Key.IsAlwaysBubbled)
				{
					// This event is always bubbled, no need to include it in the SubscribedToHandledEventsToo
					continue;
				}

				foreach (var handler in eventHandlers.Value)
				{
					if (handler.HandledEventsToo)
					{
						subscribedToHandledEventsToo |= eventHandlers.Key.Flag;
						break;
					}
				}
			}

			SubscribedToHandledEventsToo = subscribedToHandledEventsToo;
		}

		internal bool SafeRaiseEvent(RoutedEvent routedEvent, RoutedEventArgs args, BubblingContext ctx = default)
		{
			try
			{
				return RaiseEvent(routedEvent, args, ctx);
			}
			catch (Exception e)
			{
				if (this.Log().IsEnabled(LogLevel.Error))
				{
					this.Log().Error($"Failed to raise '{routedEvent.Name}': {e}");
				}

				return false;
			}
		}


		/// <summary>
		/// Raise a routed event
		/// </summary>
		/// <remarks>
		/// Return true if event is handled in managed code (shouldn't bubble natively)
		/// </remarks>
		internal bool RaiseEvent(RoutedEvent routedEvent, RoutedEventArgs args, BubblingContext ctx = default)
		{
#if TRACE_ROUTED_EVENT_BUBBLING
			Debug.Write($"{this.GetDebugIdentifier()} - [{routedEvent.Name.TrimEnd("Event")}] (ctx: {ctx})\r\n");
#endif

			if (routedEvent.Flag == RoutedEventFlag.None)
			{
				throw new InvalidOperationException($"Flag not defined for routed event {routedEvent.Name}.");
			}

			// TODO: This is just temporary workaround before proper
			// keyboard event infrastructure is implemented everywhere
			// (issue #6074)
			if (routedEvent.IsKeyEvent)
			{
				TrackKeyState(routedEvent, args);
			}

			// [3] Any local handlers?
			var isHandled = IsHandled(args);
			if (!ctx.Mode.HasFlag(BubblingMode.IgnoreElement)
				&& !ctx.IsInternal
				&& _eventHandlerStore.TryGetValue(routedEvent, out var handlers)
				&& handlers.Any())
			{
				// [4] Invoke local handlers
				foreach (var handler in handlers.ToArray())
				{
					if (!isHandled || handler.HandledEventsToo)
					{
						InvokeHandler(handler.Handler, args);
						isHandled = IsHandled(args);
					}
				}

				// [5] Event handled by local handlers?
				if (isHandled)
				{
					// [9] Any parent interested ?
					var anyParentInterested = AnyParentInterested(routedEvent);
					if (!anyParentInterested)
					{
						// [12] Event processing finished
						return true; // reported has handled in managed
					}

					// Make sure the event is marked as not bubbling natively anymore
					// --> [10]
					if (args != null)
					{
						args.CanBubbleNatively = false;
					}
				}
			}

			if (ctx.Mode.HasFlag(BubblingMode.IgnoreParents))
			{
				return isHandled;
			}

			// [6] & [7] Will the event bubbling natively or in managed code?
			var isBubblingInManagedCode = IsBubblingInManagedCode(routedEvent, args);
			if (!isBubblingInManagedCode)
			{
				return false; // [8] Return for native bubbling
			}

			// [10] Bubbling in managed code

			// Make sure the event is marked as not bubbling natively anymore
			if (args != null)
			{
				args.CanBubbleNatively = false;
			}

#if __IOS__ || __ANDROID__
			var parent = this.FindFirstParent<UIElement>();
#else
			var parent = this.GetParent() as UIElement;
#endif
			// [11] A parent is defined?
			if (parent == null)
			{
				return true; // [12] processing finished
			}

			// [13] Raise on parent
			return RaiseOnParent(routedEvent, args, parent, ctx);
		}

		private static void TrackKeyState(RoutedEvent routedEvent, RoutedEventArgs args)
		{
			if (args is KeyRoutedEventArgs keyArgs)
			{
				if (routedEvent == KeyDownEvent)
				{
					KeyboardStateTracker.OnKeyDown(keyArgs.OriginalKey);
				}
				else if (routedEvent == KeyUpEvent)
				{
					KeyboardStateTracker.OnKeyUp(keyArgs.OriginalKey);
				}
			}
		}

		// This method is a workaround for https://github.com/mono/mono/issues/12981
		// It can be inlined in RaiseEvent when fixed.
		private static bool RaiseOnParent(RoutedEvent routedEvent, RoutedEventArgs args, UIElement parent, BubblingContext ctx)
		{
			var mode = parent.PrepareManagedEventBubbling(routedEvent, args, out args);

			// If we have reached the requested root element on which this event should bubble,
			// we make sure to not allow bubbling on parents.
			if (parent == ctx.Root)
			{
				mode |= BubblingMode.IgnoreParents;
			}

			var handledByAnyParent = parent.RaiseEvent(routedEvent, args, ctx.WithMode(mode));

			return handledByAnyParent;
		}

		private BubblingMode PrepareManagedEventBubbling(RoutedEvent routedEvent, RoutedEventArgs args, out RoutedEventArgs alteredArgs)
		{
			var bubblingMode = BubblingMode.Bubble;
			alteredArgs = args;
			if (routedEvent.IsPointerEvent)
			{
				PrepareManagedPointerEventBubbling(routedEvent, ref alteredArgs, ref bubblingMode);
			}
			else if (routedEvent.IsKeyEvent)
			{
				PrepareManagedKeyEventBubbling(routedEvent, ref alteredArgs, ref bubblingMode);
			}
			else if (routedEvent.IsFocusEvent)
			{
				PrepareManagedFocusEventBubbling(routedEvent, ref alteredArgs, ref bubblingMode);
			}
			else if (routedEvent.IsManipulationEvent)
			{
				PrepareManagedManipulationEventBubbling(routedEvent, ref alteredArgs, ref bubblingMode);
			}
			else if (routedEvent.IsGestureEvent)
			{
				PrepareManagedGestureEventBubbling(routedEvent, ref alteredArgs, ref bubblingMode);
			}
			else if (routedEvent.IsDragAndDropEvent)
			{
				PrepareManagedDragAndDropEventBubbling(routedEvent, ref alteredArgs, ref bubblingMode);
			}

			return bubblingMode;
		}

		// WARNING: When implementing one of those methods to maintain a local state, you should also opt-in for RoutedEvent.IsAlwaysBubbled
		partial void PrepareManagedPointerEventBubbling(RoutedEvent routedEvent, ref RoutedEventArgs args, ref BubblingMode bubblingMode);
		partial void PrepareManagedKeyEventBubbling(RoutedEvent routedEvent, ref RoutedEventArgs args, ref BubblingMode bubblingMode);
		partial void PrepareManagedFocusEventBubbling(RoutedEvent routedEvent, ref RoutedEventArgs args, ref BubblingMode bubblingMode);
		partial void PrepareManagedManipulationEventBubbling(RoutedEvent routedEvent, ref RoutedEventArgs args, ref BubblingMode bubblingMode);
		partial void PrepareManagedGestureEventBubbling(RoutedEvent routedEvent, ref RoutedEventArgs args, ref BubblingMode bubblingMode);
		partial void PrepareManagedDragAndDropEventBubbling(RoutedEvent routedEvent, ref RoutedEventArgs args, ref BubblingMode bubblingMode);

		internal struct BubblingContext
		{
			public static readonly BubblingContext Bubble = default;

			public static readonly BubblingContext NoBubbling = new BubblingContext { Mode = BubblingMode.NoBubbling };

			/// <summary>
			/// When bubbling in managed code, the <see cref="UIElement.RaiseEvent"/> will take care to raise the event on each parent,
			/// considering the Handled flag.
			/// This value is used to flag events that are sent to element to maintain their internal state,
			/// but which are not meant to initiate a new event bubbling (a.k.a. invoke the "RaiseEvent" again)
			/// </summary>
			public static readonly BubblingContext OnManagedBubbling = new BubblingContext { Mode = BubblingMode.NoBubbling, IsInternal = true };

			public static BubblingContext BubbleUpTo(UIElement root)
				=> new BubblingContext { Root = root };

			/// <summary>
			/// The mode to use for bubbling
			/// </summary>
			public BubblingMode Mode { get; set; }

			/// <summary>
			/// An optional root element on which the bubbling should stop.
			/// </summary>
			/// <remarks>It's expected that the event is raised on this Root element.</remarks>
			public UIElement Root { get; set; }

			/// <summary>
			/// Indicates that the associated event should not be publicly raised.
			/// </summary>
			/// <remarks>
			/// The "internal" here refers only to the private state of the code which has initiated this event, not subclasses.
			/// This means that an event flagged as "internal" can bubble to update the private state of parents,
			/// but the UIElement.RoutedEvent won't be raised in any way (public and internal handlers) and it won't be sent to Control.On`RoutedEvent`() neither.
			/// </remarks>
			public bool IsInternal { get; set; }

			public BubblingContext WithMode(BubblingMode mode) => new BubblingContext
			{
				Mode = mode,
				Root = Root,
				IsInternal = IsInternal
			};

			public override string ToString()
				=> $"{Mode}{(IsInternal ? " *internal*" : "")}{(Root is { } r ? $" up to {Root.GetDebugName()}" : "")}";
		}

		/// <summary>
		/// Defines the mode used to bubble an event.
		/// </summary>
		/// <remarks>
		/// This takes priority over the <see cref="RoutedEvent.IsAlwaysBubbled"/>.
		/// Preventing default bubble behavior of an event is meant to be used only when the event has already been raised/bubbled,
		/// but we need to sent it also to some specific elements (e.g. implicit captures).
		/// </remarks>
		[Flags]
		internal enum BubblingMode
		{
			/// <summary>
			/// The event should bubble normally in this element and its parent
			/// </summary>
			Bubble = 0,

			/// <summary>
			/// The event should not be raised on current element
			/// </summary>
			IgnoreElement = 1,

			/// <summary>
			/// The event should not bubble to parent elements
			/// </summary>
			IgnoreParents = 2,

			/// <summary>
			/// The bubbling should stop here (the event won't even be raised on the current element)
			/// </summary>
			NoBubbling = IgnoreElement | IgnoreParents,
		}

		private static bool IsHandled(RoutedEventArgs args)
		{
			return args is IHandleableRoutedEventArgs cancellable && cancellable.Handled;
		}

		private bool IsBubblingInManagedCode(RoutedEvent routedEvent, RoutedEventArgs args)
		{
			if (args == null || !args.CanBubbleNatively) // [6] From platform?
			{
				// Not from platform

				return true; // -> [10] bubble in managed to parents
			}

			// [7] Event set to bubble in managed code?
			var eventsBubblingInManagedCode = EventsBubblingInManagedCode;
			var flag = routedEvent.Flag;

			return eventsBubblingInManagedCode.HasFlag(flag);
		}

		private bool AnyParentInterested(RoutedEvent routedEvent)
		{
			// Pointer events must always be dispatched to all parents in order to update visual states,
			// update manipulation, detect gestures, etc.
			// (They are then interpreted by each parent in the PrepareManagedPointerEventBubbling)
			if (routedEvent.IsAlwaysBubbled)
			{
				return true;
			}

			// [9] Any parent interested?
			var subscribedToHandledEventsToo = SubscribedToHandledEventsToo;
			var flag = routedEvent.Flag;
			return subscribedToHandledEventsToo.HasFlag(flag);
		}

		private void InvokeHandler(object handler, RoutedEventArgs args)
		{
			// TODO: WPF calls a virtual RoutedEventArgs.InvokeEventHandler(Delegate handler, object target) method,
			// instead of going through all possible cases like we do here.
			switch (handler)
			{
				case RoutedEventHandler routedEventHandler:
					routedEventHandler(this, args);
					break;
				case PointerEventHandler pointerEventHandler:
					pointerEventHandler(this, (PointerRoutedEventArgs)args);
					break;
				case TappedEventHandler tappedEventHandler:
					tappedEventHandler(this, (TappedRoutedEventArgs)args);
					break;
				case DoubleTappedEventHandler doubleTappedEventHandler:
					doubleTappedEventHandler(this, (DoubleTappedRoutedEventArgs)args);
					break;
				case RightTappedEventHandler rightTappedEventHandler:
					rightTappedEventHandler(this, (RightTappedRoutedEventArgs)args);
					break;
				case HoldingEventHandler holdingEventHandler:
					holdingEventHandler(this, (HoldingRoutedEventArgs)args);
					break;
				case DragEventHandler dragEventHandler:
					dragEventHandler(this, (global::Windows.UI.Xaml.DragEventArgs)args);
					break;
				case TypedEventHandler<UIElement, DragStartingEventArgs> dragStartingHandler:
					dragStartingHandler(this, (DragStartingEventArgs)args);
					break;
				case TypedEventHandler<UIElement, DropCompletedEventArgs> dropCompletedHandler:
					dropCompletedHandler(this, (DropCompletedEventArgs)args);
					break;
				case KeyEventHandler keyEventHandler:
					keyEventHandler(this, (KeyRoutedEventArgs)args);
					break;
				case ManipulationStartingEventHandler manipStarting:
					manipStarting(this, (ManipulationStartingRoutedEventArgs)args);
					break;
				case ManipulationStartedEventHandler manipStarted:
					manipStarted(this, (ManipulationStartedRoutedEventArgs)args);
					break;
				case ManipulationDeltaEventHandler manipDelta:
					manipDelta(this, (ManipulationDeltaRoutedEventArgs)args);
					break;
				case ManipulationInertiaStartingEventHandler manipInertia:
					manipInertia(this, (ManipulationInertiaStartingRoutedEventArgs)args);
					break;
				case ManipulationCompletedEventHandler manipCompleted:
					manipCompleted(this, (ManipulationCompletedRoutedEventArgs)args);
					break;
				case TypedEventHandler<UIElement, GettingFocusEventArgs> gettingFocusHandler:
					gettingFocusHandler(this, (GettingFocusEventArgs)args);
					break;
				case TypedEventHandler<UIElement, LosingFocusEventArgs> losingFocusHandler:
					losingFocusHandler(this, (LosingFocusEventArgs)args);
					break;
				default:
					this.Log().Error($"The handler type {handler.GetType()} has not been registered for RoutedEvent");
					break;
			}
		}

		// Those methods are part of the internal UWP API
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		internal bool ShouldRaiseEvent(Delegate eventHandler) => eventHandler != null;
	}
}
