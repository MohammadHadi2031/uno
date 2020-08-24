// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Uno.UI.Helpers.WinUI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Microsoft.UI.Xaml.Controls
{
	public partial class TabView : Control
	{
		private const double c_tabMinimumWidth = 48.0;
		private const double c_tabMaximumWidth = 200.0;

		private const string c_tabViewItemMinWidthName = "TabViewItemMinWidth";
		private const string c_tabViewItemMaxWidthName = "TabViewItemMaxWidth";

		// TODO: what is the right number and should this be customizable?
		private const double c_scrollAmount = 50.0;

		private const string SR_TabViewAddButtonName = "TabViewAddButtonName";
		private const string SR_TabViewAddButtonTooltip = "TabViewAddButtonTooltip";
		private const string SR_TabViewCloseButtonTooltip = "TabViewCloseButtonTooltip";
		private const string SR_TabViewCloseButtonTooltipWithKA = "TabViewCloseButtonTooltipWithKA";
		private const string SR_TabViewScrollDecreaseButtonTooltip = "TabViewScrollDecreaseButtonTooltip";
		private const string SR_TabViewScrollIncreaseButtonTooltip = "TabViewScrollIncreaseButtonTooltip";

		private ContentPresenter m_tabContentPresenter = null;
		private ContentPresenter m_rightContentPresenter = null;
		private ColumnDefinition m_leftContentColumn;
		private ColumnDefinition m_tabColumn;
		private ColumnDefinition m_addButtonColumn;
		private ColumnDefinition m_rightContentColumn;
		private Button m_addButton;
		private Grid m_tabContainerGrid;
		private ListView m_listView;
		private Size previousAvailableSize;

		private string m_tabCloseButtonTooltipText;

		public TabView()
		{
			var items = new ObservableVector<object>(); //TODO:MZ:Is this appropriate alternative?
			SetValue(TabItemsProperty, items);

			SetDefaultStyleKey(this);

			this.Loaded += OnLoaded;

			// KeyboardAccelerator is only available on RS3+
			if (SharedHelpers.IsRS3OrHigher())
			{
				KeyboardAccelerator ctrlf4Accel = new KeyboardAccelerator();
				ctrlf4Accel.Key = VirtualKey.F4;
				ctrlf4Accel.Modifiers = VirtualKeyModifiers.Control;
				ctrlf4Accel.Invoked += OnCtrlF4Invoked;
				ctrlf4Accel.ScopeOwner = this;
				KeyboardAccelerators.Add(ctrlf4Accel);

				m_tabCloseButtonTooltipText = ResourceAccessor.GetLocalizedStringResource(SR_TabViewCloseButtonTooltipWithKA);
			}
			else
			{
				m_tabCloseButtonTooltipText = ResourceAccessor.GetLocalizedStringResource(SR_TabViewCloseButtonTooltip);
			}

			// Ctrl+Tab as a KeyboardAccelerator only works on 19H1+
			if (SharedHelpers.Is19H1OrHigher())
			{
				KeyboardAccelerator ctrlTabAccel = new KeyboardAccelerator();
				ctrlTabAccel.Key = VirtualKey.Tab;
				ctrlTabAccel.Modifiers = VirtualKeyModifiers.Control;
				ctrlTabAccel.Invoked += OnCtrlTabInvoked;
				ctrlTabAccel.ScopeOwner = this;
				KeyboardAccelerators.Add(ctrlTabAccel);

				KeyboardAccelerator ctrlShiftTabAccel = new KeyboardAccelerator();
				ctrlShiftTabAccel.Key = VirtualKey.Tab;
				ctrlShiftTabAccel.Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
				ctrlShiftTabAccel.Invoked += OnCtrlShiftTabInvoked;
				ctrlShiftTabAccel.ScopeOwner = this;
				KeyboardAccelerators.Add(ctrlShiftTabAccel);
			}
		}

		protected override void OnApplyTemplate()
		{
			base.OnApplyTemplate();

			UnhookEventsAndClearFields();

			//IControlProtected controlProtected{ *this };

			m_tabContentPresenter = (ContentPresenter)GetTemplateChild("TabContentPresenter");
			m_rightContentPresenter = (ContentPresenter)GetTemplateChild("RightContentPresenter");

			m_leftContentColumn = (ColumnDefinition)GetTemplateChild("LeftContentColumn");
			m_tabColumn = (ColumnDefinition)GetTemplateChild("TabColumn");
			m_addButtonColumn = (ColumnDefinition)GetTemplateChild("AddButtonColumn");
			m_rightContentColumn = (ColumnDefinition)GetTemplateChild("RightContentColumn");

			var containerGrid = GetTemplateChild("TabContainerGrid") as Grid;
			if (containerGrid != null)
			{
				m_tabContainerGrid = containerGrid;
				containerGrid.PointerExited += OnTabStripPointerExited;
			}

			m_shadowReceiver = (Grid)GetTemplateChild("ShadowReceiver");

			ListView GetListView()
			{
				var listView = GetTemplateChild("TabListView") as ListView;
				if (listView != null)
				{
					//TODO:MZ:Unsubscribe when appropriate
					listView.Loaded += OnListViewLoaded;
					listView.SelectionChanged += OnListViewSelectionChanged;

					listView.DragItemsStarting += OnListViewDragItemsStarting;
					listView.DragItemsCompleted += OnListViewDragItemsCompleted;
					listView.DragOver += OnListViewDragOver;
					listView.Drop += OnListViewDrop;

					listView.GettingFocus += OnListViewGettingFocus;

					m_listViewCanReorderItemsPropertyChangedRevoker = RegisterPropertyChanged(listView, CanReorderItemsProperty, OnListViewDraggingPropertyChanged);
					m_listViewAllowDropPropertyChangedRevoker = RegisterPropertyChanged(listView, UIElement.AllowDropProperty, OnListViewDraggingPropertyChanged);
				}
				return listView;
			}
			m_listView = GetListView();

			Button GetAddButton()
			{
				var addButton = GetTemplateChild("AddButton") as Button;
				if (addButton != null)
				{
					// Do localization for the add button
					if (string.IsNullOrEmpty(AutomationProperties.GetName(addButton)))
					{
						var addButtonName = ResourceAccessor.GetLocalizedStringResource(SR_TabViewAddButtonName);
						AutomationProperties.SetName(addButton, addButtonName);
					}

					var toolTip = ToolTipService.GetToolTip(addButton);
					if (toolTip == null)
					{
						ToolTip tooltip = new ToolTip();
						tooltip.Content = ResourceAccessor.GetLocalizedStringResource(SR_TabViewAddButtonTooltip);
						ToolTipService.SetToolTip(addButton, tooltip);
					}

					addButton.Click += OnAddButtonClick; //TODO:MZ:Unsusbscribe when appropriate
				}
				return addButton;
			}
			m_addButton = GetAddButton();

			if (SharedHelpers.IsThemeShadowAvailable())
			{
				var shadowCaster = GetTemplateChild("ShadowCaster");
				if (shadowCaster != null)
				{
					ThemeShadow shadow;
					shadow.Receivers.Add(GetShadowReceiver());

					double shadowDepth = (double)SharedHelpers.FindInApplicationResources(c_tabViewShadowDepthName, c_tabShadowDepth);

					var currentTranslation = shadowCaster.Translation;
					var translation = new Vector3(currentTranslation.X, currentTranslation.Y, (float)shadowDepth);
					shadowCaster.Translation = translation;

					shadowCaster.Shadow = shadow);
				}
			}

			UpdateListViewItemContainerTransitions();
		}


		private void OnListViewDraggingPropertyChanged(DependencyObject sender, DependencyProperty args)
		{
			UpdateListViewItemContainerTransitions();
		}

		private void OnListViewGettingFocus(object sender, GettingFocusEventArgs args)
		{
			// TabViewItems overlap each other by one pixel in order to get the desired visuals for the separator.
			// This causes problems with 2d focus navigation. Because the items overlap, pressing Down or Up from a
			// TabViewItem navigates to the overlapping item which is not desired.
			//
			// To resolve this issue, we detect the case where Up or Down focus navigation moves from one TabViewItem
			// to another.
			// How we handle it, depends on the input device.
			// For GamePad, we want to move focus to something in the direction of movement (other than the overlapping item)
			// For Keyboard, we cancel the focus movement.

			var direction = args.Direction;
			if (direction == FocusNavigationDirection.Up || direction == FocusNavigationDirection.Down)
			{
				var oldItem = args.OldFocusedElement as TabViewItem;
				var newItem = args.NewFocusedElement as TabViewItem;
				if (oldItem != null && newItem != null)
				{
					var listView = m_listView;
					if (listView != null)
					{
						bool oldItemIsFromThisTabView = listView.IndexFromContainer(oldItem) != -1;
						bool newItemIsFromThisTabView = listView.IndexFromContainer(newItem) != -1;
						if (oldItemIsFromThisTabView && newItemIsFromThisTabView)
						{
							var inputDevice = args.InputDevice;
							if (inputDevice == FocusInputDeviceKind.GameController)
							{
								var listViewBoundsLocal = new Rect(0, 0, (float)listView.ActualWidth, (float)listView.ActualHeight);
								var listViewBounds = listView.TransformToVisual(null).TransformBounds(listViewBoundsLocal);
								FindNextElementOptions options = new FindNextElementOptions();
								options.ExclusionRect = listViewBounds;
								var next = FocusManager.FindNextElement(direction, options);
								var args2 = args as GettingFocusEventArgs; //TODO:MZ: IGettingFocusEventArgs2?
								if (args != null)
								{
									args2.TrySetNewFocusedElement(next);
								}

								else
								{
									// Without TrySetNewFocusedElement, we cannot set focus while it is changing.
									m_dispatcherHelper.RunAsync([next]()
		
									{
										SetFocus(next, FocusState.Programmatic);
									});
								}
								args.Handled = true;
							}
							else
							{
								args.Cancel = true;
								args.Handled = true;
							}
						}
					}
				}
			}
		}

		private void OnSelectedIndexPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateSelectedIndex();
		}

		private void OnSelectedItemPropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateSelectedItem();
		}

		private void OnTabItemsSourcePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateListViewItemContainerTransitions();
		}

		//	void UpdateListViewItemContainerTransitions()
		//	{
		//		if (TabItemsSource())
		//		{
		//			if (var listView = m_listView)
		//			{
		//				if (listView.CanReorderItems() && listView.AllowDrop())
		//				{
		//					// Remove all the AddDeleteThemeTransition/ContentThemeTransition instances in the inner ListView's ItemContainerTransitions
		//					// collection to avoid attempting to reparent a tab's content while it is still parented during a tab reordering user gesture.
		//					// This is only required when:
		//					//  - the TabViewItem' contents are databound to UIElements (this condition is not being checked below though).
		//					//  - System animations turned on (this condition is not being checked below though to maximize behavior consistency).
		//					//  - TabViewItem reordering is turned on.
		//					// With all those conditions met, the databound UIElements are still parented to the old item container as the tab is being dropped in
		//					// its new location. Without animations, the old item container is already put into the recycling pool and picked as the new container.
		//					// Its ContentControl.Content is kept unchanged and no reparenting is attempted.
		//					// Because the default ItemContainerTransitions collection is defined in the TabViewListView style, all ListView instances share the same
		//					// collection by default. Thus to avoid one TabView affecting all other ones, a new ItemContainerTransitions collection is created
		//					// when the original one contains an AddDeleteThemeTransition or ContentThemeTransition instance.
		//					bool transitionCollectionHasAddDeleteOrContentThemeTransition = [listView]()


		//				{
		//						if (var itemContainerTransitions = listView.ItemContainerTransitions())
		//                    {
		//							for (var transition : itemContainerTransitions)
		//							{
		//								if (transition &&
		//									(transition as AddDeleteThemeTransition>() || transition as ContentThemeTransition>()))
		//								{
		//									return true;
		//								}
		//							}
		//						}
		//						return false;
		//					} ();

		//					if (transitionCollectionHasAddDeleteOrContentThemeTransition)
		//					{
		//						var const newItemContainerTransitions = TransitionCollection();
		//						var const oldItemContainerTransitions = listView.ItemContainerTransitions();

		//						for (var transition : oldItemContainerTransitions)
		//						{
		//							if (transition)
		//							{
		//								if (transition as AddDeleteThemeTransition>() || transition as ContentThemeTransition>())
		//								{
		//									continue;
		//								}
		//								newItemContainerTransitions.Append(transition);
		//							}
		//						}

		//						listView.ItemContainerTransitions(newItemContainerTransitions);
		//					}
		//				}
		//			}
		//		}
		//	}

		void UnhookEventsAndClearFields()
		{
			m_listViewLoadedRevoker.revoke();
			m_listViewSelectionChangedRevoker.revoke();
			m_listViewDragItemsStartingRevoker.revoke();
			m_listViewDragItemsCompletedRevoker.revoke();
			m_listViewDragOverRevoker.revoke();
			m_listViewDropRevoker.revoke();
			m_listViewGettingFocusRevoker.revoke();
			m_listViewCanReorderItemsPropertyChangedRevoker.revoke();
			m_listViewAllowDropPropertyChangedRevoker.revoke();
			m_addButtonClickRevoker.revoke();
			m_itemsPresenterSizeChangedRevoker.revoke();
			m_tabStripPointerExitedRevoker.revoke();
			m_scrollViewerLoadedRevoker.revoke();
			m_scrollViewerViewChangedRevoker.revoke();
			m_scrollDecreaseClickRevoker.revoke();
			m_scrollIncreaseClickRevoker.revoke();

			m_tabContentPresenter.set(null);
			m_rightContentPresenter.set(null);
			m_leftContentColumn.set(null);
			m_tabColumn.set(null);
			m_addButtonColumn.set(null);
			m_rightContentColumn.set(null);
			m_tabContainerGrid.set(null);
			m_shadowReceiver.set(null);
			m_listView.set(null);
			m_addButton.set(null);
			m_itemsPresenter.set(null);
			m_scrollViewer.set(null);
			m_scrollDecreaseButton.set(null);
			m_scrollIncreaseButton.set(null);
		}

		private void OnTabWidthModePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			UpdateTabWidths();

			foreach (var item in TabItems)
			{
				// Switch the visual states of all tab items to the correct TabViewWidthMode
				TabViewItem GetTabViewItem(object item)
				{
					var tabViewItem = item as TabViewItem;
					if (tabViewItem != null)
					{
						return tabViewItem;
					}
					return ContainerFromItem(item) as TabViewItem;
				}
				var tvi = GetTabViewItem(item);

				if (tvi != null)
				{
					tvi.OnTabViewWidthModeChanged(TabWidthMode);
				}
			}
		}

		private void OnCloseButtonOverlayModePropertyChanged(DependencyPropertyChangedEventArgs args)
		{
			// Switch the visual states of all tab items to to the correct closebutton overlay mode
			foreach (var item in TabItems)
			{
				TabViewItem GetTabViewItem(object item)
				{
					var tabViewItem = item as TabViewItem;
					if (tabViewItem != null)
					{
						return tabViewItem;
					}
					return ContainerFromItem(item) as TabViewItem;
				}
				var tvi = GetTabViewItem(item);

				if (tvi != null)
				{
					tvi.OnCloseButtonOverlayModeChanged(CloseButtonOverlayMode);
				}
			}
		}

		private void OnAddButtonClick(object sender, RoutedEventArgs args)
		{
			AddTabButtonClick?.Invoke(this, args);
		}

		protected override AutomationPeer OnCreateAutomationPeer()
		{
			return new TabViewAutomationPeer(this);
		}

		private void OnLoaded(object sender, RoutedEventArgs args)
		{
			UpdateTabContent();
		}

		void OnListViewLoaded(object sender, RoutedEventArgs args)
		{
			var listView = m_listView;
			if (listView != null)
			{
				// Now that ListView exists, we can start using its Items collection.
				var lvItems = listView.Items;
				if (lvItems != null)
				{
					if (listView.ItemsSource == null)
					{
						// copy the list, because clearing lvItems may also clear TabItems
						IList<object> itemList = new List<object>(); //TODO:MZ: Is IList<object> appropriate?

						foreach (var item in TabItems)
						{
							itemList.Append(item);
						}

						lvItems.Clear();

						foreach (var item in itemList)
						{
							// App put items in our Items collection; copy them over to ListView.Items
							if (item != null)
							{
								lvItems.Append(item);
							}
						}
					}
					TabItems = lvItems;
				}

				if (ReadLocalValue(SelectedItemProperty) != DependencyProperty.UnsetValue)
				{
					UpdateSelectedItem();
				}
				else
				{
					// If SelectedItem wasn't set, default to selecting the first tab
					UpdateSelectedIndex();
				}

				SelectedIndex = listView.SelectedIndex;
				SelectedItem = listView.SelectedItem;

				// Find TabsItemsPresenter and listen for SizeChanged
				ItemsPresenter GetItemsPresenter(ListView listView)
				{
					var itemsPresenter = SharedHelpers.FindInVisualTreeByName(listView, "TabsItemsPresenter") as ItemsPresenter;
					if (itemsPresenter == null)
					{
						itemsPresenter.SizeChanged += OnItemsPresenterSizeChanged; //TODO:MZ:Unsubscribe when appropriate
					}
					return itemsPresenter;
				}
				m_itemsPresenter = GetItemsPresenter(listView);

				var scrollViewer = SharedHelpers.FindInVisualTreeByName(listView, "ScrollViewer") as FxScrollViewer;
				m_scrollViewer = scrollViewer;
				if (scrollViewer != null)
				{
					if (SharedHelpers.IsIsLoadedAvailable && scrollViewer.IsLoaded())
					{
						// This scenario occurs reliably for Terminal in XAML islands
						OnScrollViewerLoaded(null, null);
					}
					else
					{
						scrollViewer.Loaded += OnScrollViewerLoaded; //TODO:MZ:Unsubscribe when appropriate
					}
				}
			}
		}

		void OnTabStripPointerExited(object sender, PointerRoutedEventArgs args)
		{
			if (m_updateTabWidthOnPointerLeave)
			{
				try
				{
					UpdateTabWidths();
				}
				finally
				{
					m_updateTabWidthOnPointerLeave = false;
				}
			}
		}

		private void OnScrollViewerLoaded(object sender, RoutedEventArgs args)
		{
			var scrollViewer = m_scrollViewer;
			if (scrollViewer != null)
			{
				RepeatButton GetDecreaseButton(ScrollViewer scrollViewer)
				{
					var decreaseButton = SharedHelpers.FindInVisualTreeByName(scrollViewer, "ScrollDecreaseButton") as RepeatButton;
					if (decreaseButton != null)
					{
						// Do localization for the scroll decrease button
						var toolTip = ToolTipService.GetToolTip(decreaseButton);
						if (toolTip == null)
						{
							var tooltip = new ToolTip();
							tooltip.Content = ResourceAccessor.GetLocalizedStringResource(SR_TabViewScrollDecreaseButtonTooltip);
							ToolTipService.SetToolTip(decreaseButton, tooltip);
						}

						decreaseButton.Click += OnScrollDecreaseClick; //TODO:MZ:Unsubscribe somewhere
					}
					return decreaseButton;
				}
				m_scrollDecreaseButton = GetDecreaseButton(scrollViewer);


				RepeatButton GetIncreaseButton(ScrollViewer scrollViewer)
				{
					var increaseButton = SharedHelpers.FindInVisualTreeByName(scrollViewer, "ScrollIncreaseButton") as RepeatButton;
					if (increaseButton != null)
					{
						// Do localization for the scroll increase button
						var toolTip = ToolTipService.GetToolTip(increaseButton);
						if (toolTip == null)
						{
							var tooltip = new ToolTip();
							tooltip.Content = ResourceAccessor.GetLocalizedStringResource(SR_TabViewScrollIncreaseButtonTooltip);
							ToolTipService.SetToolTip(increaseButton, tooltip);
						}

						increaseButton.Click += OnScrollIncreaseClick;
					}
					return increaseButton;
				}
				m_scrollIncreaseButton = GetIncreaseButton(scrollViewer);

				scrollViewer.ViewChanged += OnScrollViewerViewChanged;
			}

			UpdateTabWidths();
		}

		void OnScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
		{
			UpdateScrollViewerDecreaseAndIncreaseButtonsViewState();
		}

		private void UpdateScrollViewerDecreaseAndIncreaseButtonsViewState()
		{
			var scrollViewer = m_scrollViewer;
			if (scrollViewer != null)
			{
				var decreaseButton = m_scrollDecreaseButton;
				var increaseButton = m_scrollIncreaseButton;

				var minThreshold = 0.1;
				var horizontalOffset = scrollViewer.HorizontalOffset();
				var scrollableWidth = scrollViewer.ScrollableWidth();

				if (Math.Abs(horizontalOffset - scrollableWidth) < minThreshold)
				{
					if (decreaseButton)
					{
						decreaseButton.IsEnabled(true);
					}
					if (increaseButton)
					{
						increaseButton.IsEnabled(false);
					}
				}
				else if (Math.Abs(horizontalOffset) < minThreshold)
				{
					if (decreaseButton)
					{
						decreaseButton.IsEnabled(false);
					}
					if (increaseButton)
					{
						increaseButton.IsEnabled(true);
					}
				}
				else
				{
					if (decreaseButton)
					{
						decreaseButton.IsEnabled(true);
					}
					if (increaseButton)
					{
						increaseButton.IsEnabled(true);
					}
				}
			}
		}

		private void OnItemsPresenterSizeChanged(object sender, SizeChangedEventArgs args)
		{
			if (!m_updateTabWidthOnPointerLeave)
			{
				// Presenter size didn't change because of item being removed, so update manually
				UpdateScrollViewerDecreaseAndIncreaseButtonsViewState();
				UpdateTabWidths();
			}
		}

		private void OnItemsChanged(object item)
		{
			var args = item as IVectorChangedEventArgs; //TODO:MZ:Is this appropriate cast?
			if (args != null)
			{
				TabItemsChanged?.Invoke(this, args);

				int numItems = TabItems.Count;

				if (args.CollectionChange == CollectionChange.ItemRemoved)
				{
					m_updateTabWidthOnPointerLeave = true;
					if (numItems > 0)
					{
						// SelectedIndex might also already be -1
						var selectedIndex = SelectedIndex;
						if (selectedIndex == -1 || selectedIndex == args.Index)
						{
							// Find the closest tab to select instead.
							int startIndex = (int)args.Index;
							if (startIndex >= numItems)
							{
								startIndex = numItems - 1;
							}
							int index = startIndex;

							do
							{
								var nextItem = ContainerFromIndex(index) as ListViewItem;

								if (nextItem != null && nextItem.IsEnabled && nextItem.Visibility == Visibility.Visible)
								{
									SelectedItem = TabItems[index];
									break;
								}

								// try the next item
								index++;
								if (index >= numItems)
								{
									index = 0;
								}
							} while (index != startIndex);
						}

					}
					// Last item removed, update sizes
					// The index of the last element is "Size() - 1", but in TabItems, it is already removed.
					if (TabWidthMode == TabViewWidthMode.Equal)
					{
						m_updateTabWidthOnPointerLeave = true;
						if (args.Index == TabItems.Count)
						{
							UpdateTabWidths(true, false);
						}
					}
				}
				else
				{
					var newItem = TabItems[(int)args.Index] as TabViewItem;
					if (newItem != null)
					{
						newItem.OnTabViewWidthModeChanged(TabWidthMode);
						newItem.SetParentTabView(this);
					}
					UpdateTabWidths();
				}
			}
		}

		private void OnListViewSelectionChanged(object sender, SelectionChangedEventArgs args)
		{
			var listView = m_listView;
			if (listView != null)
			{
				SelectedIndex = listView.SelectedIndex;
				SelectedItem = listView.SelectedItem;
			}

			UpdateTabContent();

			SelectionChanged?.Invoke(this, args);
		}

		TabViewItem FindTabViewItemFromDragItem(object item)
		{
			var tab = ContainerFromItem(item) as TabViewItem;

			if (tab == null)
			{
				var fe = item as FrameworkElement;
				if (fe != null)
				{
					tab = VisualTreeHelper.GetParent(fe) as TabViewItem;
				}
			}

			if (tab == null)
			{
				// This is a fallback scenario for tabs without a data context
				var numItems = TabItems.Count;
				for (int i = 0; i < numItems; i++)
				{
					var tabItem = ContainerFromIndex(i) as TabViewItem;
					if (tabItem.Content == item)
					{
						tab = tabItem;
						break;
					}
				}
			}

			return tab;
		}

		private void OnListViewDragItemsStarting(object sender, DragItemsStartingEventArgs args)
		{
			var item = args.Items[0];
			var tab = FindTabViewItemFromDragItem(item);
			var myArgs = new TabViewTabDragStartingEventArgs(args.Data, item, tab);

			TabDragStarting?.Invoke(this, myArgs);
		}

		private void OnListViewDragOver(object sender, DragEventArgs args)
		{
			TabStripDragOver?.Invoke(this, args);
		}

		void OnListViewDrop(object sender, DragEventArgs args)
		{
			TabStripDrop?.Invoke(this, args);
		}

		private void OnListViewDragItemsCompleted(object sender, DragItemsCompletedEventArgs args)
		{
			var item = args.Items[0]; //TODO:MZ: Needs GetAt?
			var tab = FindTabViewItemFromDragItem(item);
			var myArgs = new TabViewTabDragCompletedEventArgs(args.DropResult, item, tab);

			TabDragCompleted?.Invoke(this, myArgs);

			// None means it's outside of the tab strip area
			if (args.DropResult == DataPackageOperation.None)
			{
				var tabDroppedArgs = new TabViewTabDroppedOutsideEventArgs(item, tab);
				TabDroppedOutside?.Invoke(this, tabDroppedArgs);
			}
		}

		void UpdateTabContent()
		{
			var tabContentPresenter = m_tabContentPresenter;
			if (tabContentPresenter != null)
			{
				if (SelectedItem == null)
				{
					tabContentPresenter.Content = null;
					tabContentPresenter.ContentTemplate = null;
					tabContentPresenter.ContentTemplateSelector = null;
				}
				else
				{
					var tvi = SelectedItem as TabViewItem;
					if (tvi == null)
					{
						tvi = ContainerFromItem(SelectedItem) as TabViewItem;
					}

					if (tvi != null)
					{
						// If the focus was in the old tab content, we will lose focus when it is removed from the visual tree.
						// We should move the focus to the new tab content.
						// The new tab content is not available at the time of the LosingFocus event, so we need to
						// move focus later.
						bool shouldMoveFocusToNewTab = false;
						var revoker = tabContentPresenter.LosingFocus(auto_revoke, [&shouldMoveFocusToNewTab](object sender, LosingFocusEventArgs args)
		







						{
							shouldMoveFocusToNewTab = true;
						});

						tabContentPresenter.Content(tvi.Content());
						tabContentPresenter.ContentTemplate(tvi.ContentTemplate());
						tabContentPresenter.ContentTemplateSelector(tvi.ContentTemplateSelector());

						// It is not ideal to call UpdateLayout here, but it is necessary to ensure that the ContentPresenter has expanded its content
						// into the live visual tree.
						tabContentPresenter.UpdateLayout();

						if (shouldMoveFocusToNewTab)
						{
							var focusable = FocusManager.FindFirstFocusableElement(tabContentPresenter);
							if (!focusable)
							{
								// If there is nothing focusable in the new tab, just move focus to the TabViewItem itself.
								focusable = tvi;
							}

							if (focusable)
							{
								SetFocus(focusable, FocusState.Programmatic);
							}
						}
					}
				}
			}
		}

		private void RequestCloseTab(TabViewItem container)
		{
			var listView = m_listView;
			if (listView != null)
			{
				var args = new TabViewTabCloseRequestedEventArgs(listView.ItemFromContainer(container), container);

				TabCloseRequested?.Invoke(this, args);

				var internalTabViewItem = container;
				if (container != null)
				{
					internalTabViewItem.RaiseRequestClose(args);
				}
			}
			UpdateTabWidths(false);
		}

		private void OnScrollDecreaseClick(object sender, RoutedEventArgs args)
		{
			var scrollViewer = m_scrollViewer;
			if (scrollViewer != null)
			{
				scrollViewer.ChangeView(Math.Max(0.0, scrollViewer.HorizontalOffset - c_scrollAmount), null, null);
			}
		}

		private void OnScrollIncreaseClick(object sender, RoutedEventArgs args)
		{
			var scrollViewer = m_scrollViewer;
			if (scrollViewer != null)
			{
				scrollViewer.ChangeView(Math.Min(scrollViewer.ScrollableWidth, scrollViewer.HorizontalOffset + c_scrollAmount), null, null);
			}
		}

		protected override Size MeasureOverride(Size availableSize)
		{
			if (previousAvailableSize.Width != availableSize.Width)
			{
				previousAvailableSize = availableSize;
				UpdateTabWidths();
			}

			return base.MeasureOverride(availableSize);
		}

		void UpdateTabWidths(bool shouldUpdateWidths, bool fillAllAvailableSpace)
		{
			double tabWidth = double.NaN;

			var tabGrid = m_tabContainerGrid;
			if (tabGrid != null)
			{
				// Add up width taken by custom content and + button
				double widthTaken = 0.0;
				var leftContentColumn = m_leftContentColumn;
				if (leftContentColumn != null)
				{
					widthTaken += leftContentColumn.ActualWidth;
				}
				var addButtonColumn = m_addButtonColumn;
				if (addButtonColumn != null)
				{
					widthTaken += addButtonColumn.ActualWidth;
				}
				var rightContentColumn = m_rightContentColumn;
				if (rightContentColumn != null)
				{
					var rightContentPresenter = m_rightContentPresenter;
					if (rightContentPresenter != null)
					{
						var rightContentSize = rightContentPresenter.DesiredSize;
						rightContentColumn.MinWidth = rightContentSize.Width;
						widthTaken += rightContentSize.Width;
					}
				}

				var tabColumn = m_tabColumn;
				if (tabColumn != null)
				{
					// Note: can be infinite
					var availableWidth = previousAvailableSize.Width - widthTaken;

					// Size can be 0 when window is first created; in that case, skip calculations; we'll get a new size soon
					if (availableWidth > 0)
					{
						if (TabWidthMode == TabViewWidthMode.Equal)
						{

							var minTabWidth = SharedHelpers.FindInApplicationResources(c_tabViewItemMinWidthName, c_tabMinimumWidth);
							var maxTabWidth = SharedHelpers.FindInApplicationResources(c_tabViewItemMaxWidthName, c_tabMaximumWidth);

							// If we should fill all of the available space, use scrollviewer dimensions
							var padding = Padding;
							if (fillAllAvailableSpace)
							{
								// Calculate the proportional width of each tab given the width of the ScrollViewer.
								var tabWidthForScroller = (availableWidth - (padding.Left + padding.Right)) / (double)(TabItems.Count);
								tabWidth = std.clamp(tabWidthForScroller, minTabWidth, maxTabWidth);
							}
							else
							{
								double availableTabViewSpace = (tabColumn.ActualWidth - (padding.Left + padding.Right));
								var increaseButton = m_scrollIncreaseButton;
								if (increaseButton != null)
								{
									if (increaseButton.Visibility() == Visibility.Visible)
									{
										availableTabViewSpace -= increaseButton.ActualWidth();
									}
								}

								var decreaseButton = m_scrollDecreaseButton;
								if (decreaseButton != null)
								{
									if (decreaseButton.Visibility == Visibility.Visible)
									{
										availableTabViewSpace -= decreaseButton.ActualWidth();
									}
								}

								// Use current size to update items to fill the currently occupied space
								tabWidth = availableTabViewSpace / (double)(TabItems.Count);
							}


							// Size tab column to needed size
							tabColumn.MaxWidth = availableWidth;
							var requiredWidth = tabWidth * TabItems.Count;
							if (requiredWidth >= availableWidth)
							{
								tabColumn.Width = GridLengthHelper.FromPixels(availableWidth);
								var listview = m_listView;
								if (listview != null)
								{
									FxScrollViewer.SetHorizontalScrollBarVisibility(listview, Windows.UI.Xaml.Controls.ScrollBarVisibility.Visible);
									UpdateScrollViewerDecreaseAndIncreaseButtonsViewState();
								}
							}
							else
							{
								tabColumn.Width = GridLengthHelper.FromValueAndType(1.0, GridUnitType.Auto);
								var listview = m_listView;
								if (listview != null)
								{
									if (shouldUpdateWidths && fillAllAvailableSpace)
									{
										FxScrollViewer.SetHorizontalScrollBarVisibility(listview, Windows.UI.Xaml.Controls.ScrollBarVisibility.Hidden);
									}
									else
									{
										var decreaseButton = m_scrollDecreaseButton;
										if (decreaseButton != null)
										{
											decreaseButton.IsEnabled(false);
										}
										var increaseButton = m_scrollIncreaseButton;
										if (increaseButton != null)
										{
											increaseButton.IsEnabled(false);
										}
									}
								}
							}
						}
						else
						{
							// Case: TabWidthMode "Compact" or "FitToContent"
							tabColumn.MaxWidth = availableWidth;
							tabColumn.Width = GridLengthHelper.FromValueAndType(1.0, GridUnitType.Auto);
							var listview = m_listView;
							if (listview != null)
							{
								listview.MaxWidth = availableWidth;

								// Calculate if the scroll buttons should be visible.
								var itemsPresenter = m_itemsPresenter;
								if (itemsPresenter != null)
								{
									var visible = itemsPresenter.ActualWidth > availableWidth;
									FxScrollViewer.SetHorizontalScrollBarVisibility(listview, visible
										? Windows.UI.Xaml.Controls.ScrollBarVisibility.Visible
										: Windows.UI.Xaml.Controls.ScrollBarVisibility.Hidden);
									if (visible)
									{
										UpdateScrollViewerDecreaseAndIncreaseButtonsViewState();
									}
								}
							}
						}
					}
				}
			}


			if (shouldUpdateWidths || TabWidthMode != TabViewWidthMode.Equal)
			{
				foreach (var item in TabItems)
				{
					// Set the calculated width on each tab.
					var tvi = item as TabViewItem;
					if (tvi == null)
					{
						tvi = ContainerFromItem(item) as TabViewItem;
					}

					if (tvi != null)
					{
						tvi.Width = tabWidth;
					}
				}
			}
		}

		void UpdateSelectedItem()
		{
			var listView = m_listView;
			if (listView != null)
			{
				var tvi = SelectedItem as TabViewItem;
				if (tvi == null)
				{
					tvi = ContainerFromItem(SelectedItem) as TabViewItem;
				}

				if (tvi != null)
				{
					listView.SelectedItem = tvi;

					// Setting ListView.SelectedItem will not work here in all cases.
					// The reason why that doesn't work but this does is unknown.
					tvi.IsSelected = true;
				}
			}
		}

		private void UpdateSelectedIndex()
		{
			var listView = m_listView;
			if (listView != null)
			{
				listView.SelectedIndex = SelectedIndex;
			}
		}

		private DependencyObject ContainerFromItem(object item)
		{
			var listView = m_listView;
			if (listView != null)
			{
				return listView.ContainerFromItem(item);
			}
			return null;
		}

		private DependencyObject ContainerFromIndex(int index)
		{
			var listView = m_listView;
			if (listView != null)
			{
				return listView.ContainerFromIndex(index);
			}
			return null;
		}

		private object ItemFromContainer(DependencyObject container)
		{
			var listView = m_listView;
			if (listView != null)
			{
				return listView.ItemFromContainer(container);
			}
			return null;
		}

		private int GetItemCount()
		{
			var itemsSource = TabItemsSource;
			if (itemsSource != null)
			{
				var iterable = itemsSource as IEnumerable<DependencyObject>; //TODO:MZ:Verify cast is correct
				if (iterable != null)
				{
					//int i = 1;
					//var iter = iterable.First();
					//while (iter.MoveNext())
					//{
					//	i++;
					//}
					//return i;
					return iterable.Count();
				}
				return 0;
			}

			else
			{
				return (int)TabItems.Count;
			}
		}

		private bool SelectNextTab(int increment)
		{
			bool handled = false;
			int itemsSize = GetItemCount();
			if (itemsSize > 1)
			{
				var index = SelectedIndex;
				index = (index + increment + itemsSize) % itemsSize;
				SelectedIndex = index;
				handled = true;
			}
			return handled;
		}

		private bool RequestCloseCurrentTab()
		{
			bool handled = false;
			var selectedTab = SelectedItem as TabViewItem;
			if (selectedTab != null)
			{
				if (selectedTab.IsClosable)
				{
					// Close the tab on ctrl + F4
					RequestCloseTab(selectedTab);
					handled = true;
				}
			}

			return handled;
		}

		protected override void OnKeyDown(KeyRoutedEventArgs args)
		{
			var coreWindow = CoreWindow.GetForCurrentThread();
			if (coreWindow != null)
			{
				if (args.Key == VirtualKey.F4)
				{
					// Handle Ctrl+F4 on RS2 and lower
					// On RS3+, it is handled by a KeyboardAccelerator
					if (!SharedHelpers.IsRS3OrHigher())
					{
						var isCtrlDown = (coreWindow.GetKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
						if (isCtrlDown)
						{
							args.Handled = RequestCloseCurrentTab();
						}
					}
				}
				else if (args.Key == VirtualKey.Tab)
				{
					// Handle Ctrl+Tab/Ctrl+Shift+Tab on RS5 and lower
					// On 19H1+, it is handled by a KeyboardAccelerator
					if (!SharedHelpers.Is19H1OrHigher())
					{
						var isCtrlDown = (coreWindow.GetKeyState(VirtualKey.Control) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
						var isShiftDown = (coreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

						if (isCtrlDown && !isShiftDown)
						{
							args.Handled = SelectNextTab(1);
						}
						else if (isCtrlDown && isShiftDown)
						{
							args.Handled = SelectNextTab(-1);
						}
					}
				}
			}
		}

		private void OnCtrlF4Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			args.Handled = RequestCloseCurrentTab();
		}

		private void OnCtrlTabInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			args.Handled = SelectNextTab(1);
		}

		private void OnCtrlShiftTabInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
		{
			args.Handled = SelectNextTab(-1);
		}
	}
}
