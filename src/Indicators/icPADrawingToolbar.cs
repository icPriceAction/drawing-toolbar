// =========================================================================================
// Tool Name:    icPA - Drawing Toolbar
// Class:        icPADrawingToolbar
// Category:     Indicator
// Version:      1.0.2
// Platform:     NinjaTrader 8.1+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/drawing-toolbar
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
// Prop Code:    ICPAT (Discounts: https://icpriceaction.com/prop-firms.html)
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  A drawing tool toolbar that floats on the chart or docks in the chart window's toolbar,
//               with your own choice and order of tools plus optional buttons to hide/show or delete
//               all hand-made drawings on the chart.
//
// Changelog:
//   v1.0.2 (2026-11-03) - Initial public release. Cleaned and optimized by icPriceAction.
//                       - Renamed to icPADrawingToolbar (icPA folder); settings grouped under "1. General".
//                       - Fix: a saved hidden state is no longer re-applied while the hide/show button is turned off.
//                       - Fix: all UI cleanup on removal now runs on the UI thread (no race with drag/resize handlers); mouse capture released.
//                       - Errors from toolbar buttons are logged to the Output window instead of escaping to the UI thread.
//                       - Drawing tool catalog is scanned once per instance instead of on every refresh.
//                       - Replaced C# 8/9-only syntax with equivalent older syntax for wider compiler compatibility.
//   v1.0.1 - Tool lists in "Configure tools..." scroll when long; the window sizes itself to its content.
//   v1.0.0 - Drawings hidden by the toolbar are shown again when it is removed or the hide/show button is turned off; Segoe Fluent Icons with Segoe MDL2 fallback for action buttons; code cleanup.
//   v0.3.1 - Delete confirmation states that deleting cannot be undone.
//   v0.3.0 - Optional hide/show and delete-all drawing buttons; hidden state saved with the workspace.
//   v0.2.1 - Fixed a compile error in the theme resource helper.
//   v0.2.0 - Rewrite; "Configure tools..." window to choose and order tools; safer tool loading and cleanup.
//   v0.1.0 - Initial version: floating toolbar with optional docking in the chart window's toolbar.
// =========================================================================================
//
// Usage notes
//   - Click a tool button to start drawing with that tool.
//   - Right-click the grip (the three dots) and choose "Configure tools..." to pick the tools and their order.
//   - Double-click the grip to open the indicator properties.
//   - Floating mode: drag the grip to move the toolbar; drag it off the panel to move it to another panel.
//   - Hide/show and delete only affect drawings made by hand on this chart. Global drawings and drawings
//     created by indicators or strategies are never touched. Delete also keeps locked drawings. Hide/show
//     only restores the drawings it hid, so drawings you hid yourself stay hidden.
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
#endregion

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
	// Referenced by full name (not typeof) so NT8's property grid can resolve the converter after each recompile.
	[TypeConverter("NinjaTrader.NinjaScript.Indicators.icPA.icPADrawingToolbarConverter")]
	[Gui.CategoryOrder("1. General", 1)]
	[CategoryOrder(typeof(Resource), "PropertyCategoryDataSeries", 2)]
	[CategoryOrder(typeof(Resource), "NinjaScriptSetup", 3)]
	[CategoryOrder(typeof(Custom.Resource), "NinjaScriptIndicatorVisualGroup", 4)]
	public class icPADrawingToolbar : Indicator
	{
		public const string Version = "1.0.2";

		private const char		ToolListSeparator	= ';';
		private const char		HiddenTagSeparator	= '\n';
		private const double	DefaultFloatingTop	= 25;

		// Icon font for the action buttons; Segoe Fluent Icons (Windows 11) and Segoe MDL2 Assets (Windows 10) share these code points.
		private const string	ActionIconFont		= "Segoe Fluent Icons, Segoe MDL2 Assets";
		private const string	GlyphDelete			= "\uE74D";
		private const string	GlyphVisible		= "\uE890";
		private const string	GlyphHidden			= "\uED1A";

		private Dispatcher								uiDispatcher;
		private Border									toolsSlot;

		private NinjaTrader.Gui.Chart.Chart				chartWindow;
		private FrameworkElement						dockedRoot;

		private Grid									floatingRoot;
		private Border									floatingGrip;
		private NinjaTrader.Gui.Chart.ChartPanel		floatingPanel;
		private Point									dragStartPointer;
		private Point									dragStartOffset;

		private Button									hideToggleButton;
		private MethodInfo								removeDrawingMethod;
		private bool									removeMethodSearched;

		private Dictionary<string, ToolInfo>			toolCatalog;

		#region Lifecycle

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name						= "icPA - Drawing Toolbar";
				Description					= "Drawing tool toolbar that floats on the chart or docks in the chart window's toolbar. Right-click the grip to choose and order the drawing tools. Optional buttons hide/show or delete all hand-made drawings on the chart (global, locked and script-drawn drawings are never touched)."
											+ "\r\nDeveloped by Istvan Cebrian | https://icpriceaction.com"
											+ "\rSupport this free tool: https://buymeacoffee.com/icpriceaction | Prop Firm Discount Code: ICPAT (https://icpriceaction.com/prop-firms.html)";
				IsOverlay					= true;
				IsChartOnly					= true;
				DisplayInDataBox			= false;
				PaintPriceMarkers			= false;
				IsSuspendedWhileInactive	= true;

				ToolList = string.Join(ToolListSeparator.ToString(), new[]
				{
					typeof(DrawingTools.Line).FullName,
					typeof(DrawingTools.Ray).FullName,
					typeof(DrawingTools.ExtendedLine).FullName,
					typeof(DrawingTools.HorizontalLine).FullName,
					typeof(DrawingTools.VerticalLine).FullName,
					typeof(DrawingTools.FibonacciRetracements).FullName,
					typeof(DrawingTools.FibonacciExtensions).FullName,
					typeof(DrawingTools.Rectangle).FullName,
					typeof(DrawingTools.Ellipse).FullName,
					typeof(DrawingTools.Text).FullName
				});

				DockInToolbar			= true;
				ToolbarOffset			= 20;
				NumberOfRows			= 5;
				IsVisibleOnlyFocused	= false;
				ShowHideToggleButton	= true;
				ShowDeleteAllButton		= true;
				DrawingsHidden			= false;
				HiddenDrawingTags		= string.Empty;
				Left					= 5;
				Top						= -1;	// -1 means "not positioned yet"
			}
			else if (State == State.Historical)
			{
				if (!IsVisible || ChartControl == null)
					return;

				uiDispatcher = ChartControl.Dispatcher;
				uiDispatcher.InvokeAsync(() =>
				{
					if (State >= State.Terminated)
						return;

					if (DockInToolbar)
						BuildDocked();
					else
						BuildFloating();
				});
			}
			else if (State == State.Realtime)
			{
				// The chart's drawings are loaded by now, so a saved hidden state can be applied.
				if (uiDispatcher != null)
				{
					uiDispatcher.InvokeAsync(() =>
					{
						if (State < State.Terminated)
							ReapplyHiddenState();
					});
				}
			}
			else if (State == State.Terminated)
			{
				// Terminated also runs on instances that never built any UI; those have no dispatcher.
				if (uiDispatcher == null)
					return;

				// All UI fields are read and cleared on the UI thread, so no drag or resize handler can see them half torn down.
				uiDispatcher.InvokeAsync(CleanUpUi);
			}
		}

		private void CleanUpUi()
		{
			try
			{
				if (chartWindow != null)
				{
					if (chartWindow.MainTabControl != null)
						chartWindow.MainTabControl.SelectionChanged -= OnTabSelectionChanged;
					if (dockedRoot != null && chartWindow.MainMenu.Contains(dockedRoot))
						chartWindow.MainMenu.Remove(dockedRoot);
				}

				if (floatingPanel != null)
					floatingPanel.SizeChanged -= OnPanelSizeChanged;

				if (floatingGrip != null && floatingGrip.IsMouseCaptured)
					floatingGrip.ReleaseMouseCapture();
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: cleanup failed ({1})", Name, ex.Message));
			}
			finally
			{
				chartWindow			= null;
				dockedRoot			= null;
				floatingRoot		= null;
				floatingGrip		= null;
				floatingPanel		= null;
				toolsSlot			= null;
				hideToggleButton	= null;
			}

			// Never leave drawings hidden once the toolbar is gone. The saved state is kept, so a
			// reloaded workspace or a re-applied toolbar hides them again.
			if (DrawingsHidden)
				RestoreHiddenDrawings();
		}

		protected override void OnBarUpdate() { }

		public override void CopyTo(NinjaScript ninjaScript)
		{
			icPADrawingToolbar other = ninjaScript as icPADrawingToolbar;
			if (other != null)
			{
				other.Left				= Left;
				other.Top				= Top;
				other.ToolList			= ToolList;
				other.DrawingsHidden	= DrawingsHidden;
				other.HiddenDrawingTags	= HiddenDrawingTags;
			}
			base.CopyTo(ninjaScript);
		}

		// Runs a UI action and logs any error to the Output window instead of letting it escape on the UI thread.
		private void SafeRun(string what, Action action)
		{
			try
			{
				action();
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: {1} failed ({2})", Name, what, (ex.InnerException ?? ex).Message));
			}
		}

		#endregion

		#region Docked mode

		private void BuildDocked()
		{
			chartWindow = Window.GetWindow(ChartControl) as NinjaTrader.Gui.Chart.Chart;
			if (chartWindow == null)
				return;

			toolsSlot = new Border { VerticalAlignment = VerticalAlignment.Center };

			StackPanel root = new StackPanel
			{
				Orientation			= Orientation.Horizontal,
				VerticalAlignment	= VerticalAlignment.Center,
				Margin				= new Thickness(ToolbarOffset, 0, 0, 0)
			};
			root.Children.Add(CreateGrip(false));
			root.Children.Add(toolsSlot);
			dockedRoot = root;

			RefreshTools();
			chartWindow.MainMenu.Add(root);

			// All tabs in a chart window share one toolbar, so the toolbar is only shown while its own tab is selected.
			if (chartWindow.MainTabControl != null)
				chartWindow.MainTabControl.SelectionChanged += OnTabSelectionChanged;
			UpdateDockedVisibility();
		}

		private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (chartWindow != null && e.OriginalSource == chartWindow.MainTabControl)
				UpdateDockedVisibility();
		}

		private void UpdateDockedVisibility()
		{
			if (dockedRoot != null)
				dockedRoot.Visibility = IsOwnTabSelected() ? Visibility.Visible : Visibility.Collapsed;
		}

		private bool IsOwnTabSelected()
		{
			if (chartWindow == null || chartWindow.MainTabControl == null)
				return false;

			TabItem tab = chartWindow.MainTabControl.SelectedItem as TabItem;
			if (tab == null)
				return false;

			ChartTab chartTab = tab.Content as ChartTab;
			return chartTab != null && chartTab.ChartControl == ChartControl;
		}

		#endregion

		#region Floating mode

		private void BuildFloating()
		{
			floatingPanel = ChartPanel;
			if (floatingPanel == null)
				return;

			toolsSlot = new Border
			{
				Background		= Res<Brush>("BackgroundMainWindow", Brushes.White),
				BorderBrush		= Res<Brush>("BorderThinBrush", Brushes.Gray),
				BorderThickness	= new Thickness(ResDouble("BorderThinThickness", 1))
			};

			floatingGrip = CreateGrip(true);
			floatingGrip.MouseMove			+= OnFloatingGripMouseMove;
			floatingGrip.MouseLeftButtonUp	+= (s, e) =>
			{
				if (floatingGrip != null)
					floatingGrip.ReleaseMouseCapture();
			};

			floatingRoot = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
			floatingRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			floatingRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Grid.SetColumn(floatingGrip, 1);
			floatingRoot.Children.Add(toolsSlot);
			floatingRoot.Children.Add(floatingGrip);

			RefreshTools();

			if (IsVisibleOnlyFocused && ChartControl.OwnerChart != null)
			{
				floatingRoot.SetBinding(UIElement.VisibilityProperty, new Binding("IsActive")
				{
					Source		= ChartControl.OwnerChart,
					Converter	= new BooleanToVisibilityConverter()
				});
			}

			double startTop = Top < 0 ? DefaultFloatingTop : Top;
			floatingRoot.Margin = new Thickness(Math.Max(0, Left), startTop, 0, 0);
			UserControlCollection.Add(floatingRoot);

			// Once the toolbar has a size, keep it inside the panel and clear of the "delayed data" button.
			floatingRoot.Loaded += (s, e) =>
			{
				if (floatingRoot != null)
					MoveFloatingTo(floatingRoot.Margin.Left, floatingRoot.Margin.Top);
			};
			floatingPanel.SizeChanged += OnPanelSizeChanged;
		}

		private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
		{
			if (floatingRoot != null)
				MoveFloatingTo(floatingRoot.Margin.Left, floatingRoot.Margin.Top);
		}

		private void OnFloatingGripMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
		{
			if (floatingGrip == null || floatingPanel == null || !floatingGrip.IsMouseCaptured)
				return;

			Point p = e.GetPosition(floatingPanel);

			// Dragging past the panel edge hands over to the chart's drag and drop, so the toolbar can move to another panel.
			if (p.X < 0 || p.Y < 0 || p.X > floatingPanel.ActualWidth || p.Y > floatingPanel.ActualHeight)
			{
				floatingGrip.ReleaseMouseCapture();
				if (ChartControl != null)
					SafeRun("move to another panel", () => ChartControl.InitDragDrop(this));
				return;
			}

			MoveFloatingTo(dragStartOffset.X + (p.X - dragStartPointer.X), dragStartOffset.Y + (p.Y - dragStartPointer.Y));
		}

		private void MoveFloatingTo(double left, double top)
		{
			if (floatingRoot == null || floatingPanel == null)
				return;

			if (floatingPanel.ActualWidth > 0 && floatingPanel.ActualHeight > 0)
			{
				double maxLeft	= Math.Max(0, floatingPanel.ActualWidth - floatingRoot.ActualWidth);
				double maxTop	= Math.Max(0, floatingPanel.ActualHeight - floatingRoot.ActualHeight);

				left	= Math.Min(Math.Max(0, left), maxLeft);
				top		= Math.Min(Math.Max(0, top), maxTop);

				if (floatingPanel.IsDelayedButtonVisible && left < floatingPanel.DelayedButtonWidth && top < floatingPanel.DelayedButtonHeight)
					top = Math.Min(floatingPanel.DelayedButtonHeight, maxTop);
			}

			floatingRoot.Margin	= new Thickness(left, top, 0, 0);
			Left				= left;
			Top					= top;
		}

		#endregion

		#region Grip and menu

		private Border CreateGrip(bool floating)
		{
			Brush dotBrush = Res<Brush>("BorderThinBrush", Brushes.Gray);

			StackPanel dots = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
			for (int i = 0; i < 3; i++)
				dots.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Margin = new Thickness(0, 1.5, 0, 1.5), Fill = dotBrush });

			Border grip = new Border
			{
				Background	= floating ? Res<Brush>("BackgroundMainWindow", Brushes.White) : Brushes.Transparent,
				Padding		= new Thickness(4, 2, 4, 2),
				Cursor		= floating ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Hand,
				ToolTip		= floating ? "Drag to move · Double-click: properties · Right-click: configure tools" : "Double-click: properties · Right-click: configure tools",
				Child		= dots
			};

			if (floating)
			{
				grip.BorderBrush		= dotBrush;
				grip.BorderThickness	= new Thickness(0, 1, 1, 1);
			}

			ContextMenu menu = CreateGripMenu();
			grip.MouseRightButtonDown	+= (s, e) => e.Handled = true;
			grip.MouseRightButtonUp		+= (s, e) =>
			{
				menu.PlacementTarget	= grip;
				menu.IsOpen				= true;
				e.Handled				= true;
			};

			grip.MouseLeftButtonDown += (s, e) =>
			{
				if (e.ClickCount > 1)
				{
					grip.ReleaseMouseCapture();
					OpenIndicatorProperties();
					e.Handled = true;
					return;
				}

				if (floating && floatingRoot != null && floatingPanel != null)
				{
					dragStartPointer	= e.GetPosition(floatingPanel);
					dragStartOffset		= new Point(floatingRoot.Margin.Left, floatingRoot.Margin.Top);
					grip.CaptureMouse();
					e.Handled			= true;
				}
			};

			return grip;
		}

		private ContextMenu CreateGripMenu()
		{
			MenuItem configure = new MenuItem { Header = "Configure tools..." };
			configure.Click += (s, e) => SafeRun("configure tools", OpenToolConfigWindow);

			MenuItem properties = new MenuItem { Header = "Indicator properties..." };
			properties.Click += (s, e) => OpenIndicatorProperties();

			ContextMenu menu = new ContextMenu();
			menu.Items.Add(configure);
			menu.Items.Add(properties);
			return menu;
		}

		private void OpenIndicatorProperties()
		{
			if (ChartControl != null)
				SafeRun("open indicator properties", () => ChartControl.OnIndicatorsHotKey(this, null));
		}

		#endregion

		#region Tool buttons

		// Rebuilds the toolbar's buttons from the saved tool list and the action button settings.
		private void RefreshTools()
		{
			if (toolsSlot == null)
				return;

			List<ToolInfo>	tools		= GetSelectedTools(GetCatalog());
			UIElement		toolArea	= DockInToolbar ? BuildToolStrip(tools) : BuildToolGrid(tools);
			UIElement		actions		= BuildActionButtons(DockInToolbar);

			if (actions == null)
				toolsSlot.Child = toolArea;
			else
			{
				StackPanel combined = new StackPanel { Orientation = Orientation.Horizontal };
				combined.Children.Add(toolArea);
				if (tools.Count > 0)
					combined.Children.Add(CreateSeparator(DockInToolbar));
				combined.Children.Add(actions);
				toolsSlot.Child = combined;
			}

			if (!DockInToolbar && floatingRoot != null && floatingRoot.IsLoaded)
			{
				floatingRoot.UpdateLayout();
				MoveFloatingTo(floatingRoot.Margin.Left, floatingRoot.Margin.Top);
			}
		}

		private UIElement BuildToolStrip(List<ToolInfo> tools)
		{
			StackPanel strip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
			foreach (ToolInfo tool in tools)
				strip.Children.Add(CreateToolButton(tool, true));
			return strip;
		}

		// Fills column by column, with NumberOfRows buttons per column.
		private UIElement BuildToolGrid(List<ToolInfo> tools)
		{
			Grid	grid	= new Grid();
			int		rows	= Math.Max(1, NumberOfRows);

			for (int i = 0; i < tools.Count; i++)
			{
				int row		= i % rows;
				int column	= i / rows;

				while (grid.RowDefinitions.Count <= row)
					grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				while (grid.ColumnDefinitions.Count <= column)
					grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

				Button button = CreateToolButton(tools[i], false);
				Grid.SetRow(button, row);
				Grid.SetColumn(button, column);
				grid.Children.Add(button);
			}

			return grid;
		}

		private Button CreateToolButton(ToolInfo tool, bool docked)
		{
			// A fresh instance per button, because a tool's icon can be a visual element that only one parent may hold.
			object icon = null;
			try
			{
				DrawingTools.DrawingTool instance = Activator.CreateInstance(tool.Type) as DrawingTools.DrawingTool;
				icon = instance != null ? instance.Icon : null;
			}
			catch
			{
				icon = null;
			}

			Button button = new Button
			{
				Content				= icon ?? Gui.Tools.Icons.DrawPencil,
				ToolTip				= tool.Tooltip,
				Style				= Res<Style>("LinkButtonStyle", null),
				FontFamily			= Res<FontFamily>("IconsFamily", SystemFonts.MessageFontFamily),
				FontSize			= 16,
				FontStyle			= FontStyles.Normal,
				Margin				= docked ? new Thickness(1, 0, 1, 0) : new Thickness(3),
				Padding				= docked ? new Thickness(3, 0, 3, 0) : new Thickness(3),
				VerticalAlignment	= VerticalAlignment.Center
			};

			string typeName = tool.FullName;
			string toolName = tool.Name;
			button.Click += (s, e) =>
			{
				if (ChartControl != null)
					SafeRun("start drawing " + toolName, () => ChartControl.TryStartDrawing(typeName));
			};

			return button;
		}

		#endregion

		#region Action buttons

		private UIElement BuildActionButtons(bool docked)
		{
			hideToggleButton = null;

			if (!ShowHideToggleButton && !ShowDeleteAllButton)
				return null;

			StackPanel panel = new StackPanel
			{
				Orientation			= docked ? Orientation.Horizontal : Orientation.Vertical,
				VerticalAlignment	= docked ? VerticalAlignment.Center : VerticalAlignment.Top
			};

			if (ShowHideToggleButton)
			{
				hideToggleButton = CreateActionButton(GlyphVisible, docked, "hide/show drawings", ToggleDrawingsVisibility);
				UpdateHideToggleButton();
				panel.Children.Add(hideToggleButton);
			}

			if (ShowDeleteAllButton)
			{
				Button delete = CreateActionButton(GlyphDelete, docked, "delete all drawings", DeleteAllDrawings);
				delete.ToolTip = "Delete all drawings on this chart";
				panel.Children.Add(delete);
			}

			return panel;
		}

		private Button CreateActionButton(string glyph, bool docked, string actionName, Action onClick)
		{
			Button button = new Button
			{
				Content				= glyph,
				Style				= Res<Style>("LinkButtonStyle", null),
				FontFamily			= new FontFamily(ActionIconFont),
				FontSize			= 14,
				FontStyle			= FontStyles.Normal,
				Margin				= docked ? new Thickness(1, 0, 1, 0) : new Thickness(3),
				Padding				= docked ? new Thickness(3, 0, 3, 0) : new Thickness(3),
				VerticalAlignment	= VerticalAlignment.Center
			};
			button.Click += (s, e) => SafeRun(actionName, onClick);
			return button;
		}

		private UIElement CreateSeparator(bool docked)
		{
			return new Border
			{
				Width				= 1,
				Height				= docked ? 16 : double.NaN,
				VerticalAlignment	= docked ? VerticalAlignment.Center : VerticalAlignment.Stretch,
				Margin				= docked ? new Thickness(4, 0, 4, 0) : new Thickness(2, 4, 2, 4),
				Background			= Res<Brush>("BorderThinBrush", Brushes.Gray)
			};
		}

		private void UpdateHideToggleButton()
		{
			if (hideToggleButton == null)
				return;

			hideToggleButton.Content	= DrawingsHidden ? GlyphHidden : GlyphVisible;
			hideToggleButton.ToolTip	= DrawingsHidden ? "Show the drawings hidden by this toolbar" : "Hide all drawings on this chart";
		}

		// Hand-made, non-global drawings on this chart. Drawings created by scripts belong to those scripts and are left alone.
		private List<DrawingTools.DrawingTool> GetUserDrawings()
		{
			if (DrawObjects == null)
				return new List<DrawingTools.DrawingTool>();

			return DrawObjects.ToList()
				.OfType<DrawingTools.DrawingTool>()
				.Where(d => d.IsUserDrawn && !d.IsGlobalDrawingTool)
				.ToList();
		}

		private HashSet<string> GetHiddenTags()
		{
			return new HashSet<string>((HiddenDrawingTags ?? string.Empty).Split(HiddenTagSeparator).Where(t => t.Length > 0), StringComparer.Ordinal);
		}

		private void SetHiddenTags(IEnumerable<string> tags)
		{
			HiddenDrawingTags	= string.Join(HiddenTagSeparator.ToString(), tags);
			DrawingsHidden		= HiddenDrawingTags.Length > 0;
		}

		private void ToggleDrawingsVisibility()
		{
			if (DrawingsHidden)
			{
				RestoreHiddenDrawings();
				SetHiddenTags(Enumerable.Empty<string>());
			}
			else
			{
				List<string> hidden = new List<string>();
				foreach (DrawingTools.DrawingTool drawing in GetUserDrawings())
				{
					if (!drawing.IsVisible)
						continue;
					drawing.IsVisible = false;
					hidden.Add(drawing.Tag);
				}

				SetHiddenTags(hidden);
			}

			UpdateHideToggleButton();
			ForceRefresh();
		}

		// Makes the drawings this toolbar hid visible again, without changing the saved hidden state.
		private void RestoreHiddenDrawings()
		{
			try
			{
				HashSet<string> tags = GetHiddenTags();
				foreach (DrawingTools.DrawingTool drawing in GetUserDrawings())
					if (tags.Contains(drawing.Tag))
						drawing.IsVisible = true;
			}
			catch (Exception ex)
			{
				Print(string.Format("{0}: could not restore hidden drawings ({1})", Name, ex.Message));
			}
		}

		// Re-hides the saved drawings on load, but only while the hide/show button is enabled, so drawings
		// are never left hidden without a button to show them again.
		private void ReapplyHiddenState()
		{
			if (DrawingsHidden && ShowHideToggleButton)
			{
				HashSet<string> tags = GetHiddenTags();
				foreach (DrawingTools.DrawingTool drawing in GetUserDrawings())
					if (tags.Contains(drawing.Tag))
						drawing.IsVisible = false;
				ForceRefresh();
			}

			UpdateHideToggleButton();
		}

		private void DeleteAllDrawings()
		{
			Window							owner	= Window.GetWindow(ChartControl);
			List<DrawingTools.DrawingTool>	targets	= GetUserDrawings().Where(d => !d.IsLocked).ToList();

			if (targets.Count == 0)
			{
				NinjaTrader.Gui.Tools.NTMessageBoxSimple.Show(owner,
					"There are no drawings to delete on this chart.\n\nGlobal and locked drawings are never deleted.",
					"Delete all drawings", MessageBoxButton.OK, MessageBoxImage.Information);
				return;
			}

			string question = string.Format(
				"Delete {0} drawing{1} from this chart?\n\nGlobal and locked drawings are kept. This cannot be undone.",
				targets.Count, targets.Count == 1 ? string.Empty : "s");

			if (NinjaTrader.Gui.Tools.NTMessageBoxSimple.Show(owner, question, "Delete all drawings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
				return;

			foreach (DrawingTools.DrawingTool drawing in targets)
				RemoveUserDrawing(drawing);

			// Forget hidden tags whose drawings no longer exist.
			List<DrawingTools.DrawingTool>	remaining		= GetUserDrawings();
			HashSet<string>					remainingTags	= new HashSet<string>(remaining.Select(d => d.Tag), StringComparer.Ordinal);
			if (DrawingsHidden)
			{
				SetHiddenTags(GetHiddenTags().Where(remainingTags.Contains));
				UpdateHideToggleButton();
			}

			ForceRefresh();

			int notRemoved = targets.Count(d => remaining.Contains(d));
			if (notRemoved > 0)
			{
				NinjaTrader.Gui.Tools.NTMessageBoxSimple.Show(owner,
					string.Format("{0} drawing{1} could not be deleted. See the NinjaScript Output window for details.", notRemoved, notRemoved == 1 ? string.Empty : "s"),
					"Delete all drawings", MessageBoxButton.OK, MessageBoxImage.Warning);
			}
		}

		// NinjaScript has no documented way to remove a hand-made drawing. The chart's own RemoveDrawingTool method
		// is not public API, so it is located by reflection; if it can't be found, RemoveDrawObject(tag) is used instead.
		private void RemoveUserDrawing(DrawingTools.DrawingTool drawing)
		{
			if (!removeMethodSearched)
			{
				removeMethodSearched	= true;
				removeDrawingMethod		= typeof(NinjaTrader.Gui.Chart.ChartControl)
					.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
					.FirstOrDefault(m =>
					{
						if (m.Name != "RemoveDrawingTool")
							return false;
						ParameterInfo[] p = m.GetParameters();
						return p.Length >= 1
							&& p[0].ParameterType.IsAssignableFrom(typeof(DrawingTools.DrawingTool))
							&& p.Skip(1).All(x => x.ParameterType == typeof(bool));
					});

				if (removeDrawingMethod == null)
					Print(Name + ": ChartControl.RemoveDrawingTool not found; falling back to RemoveDrawObject.");
			}

			if (removeDrawingMethod != null && ChartControl != null)
			{
				try
				{
					object[] args = new object[removeDrawingMethod.GetParameters().Length];
					args[0] = drawing;
					for (int i = 1; i < args.Length; i++)
						args[i] = false;

					removeDrawingMethod.Invoke(ChartControl, args);
					return;
				}
				catch (Exception ex)
				{
					Print(string.Format("{0}: could not remove drawing '{1}' ({2})", Name, drawing.Tag, (ex.InnerException ?? ex).Message));
				}
			}

			try { RemoveDrawObject(drawing.Tag); }
			catch (Exception ex) { Print(string.Format("{0}: RemoveDrawObject failed for '{1}' ({2})", Name, drawing.Tag, ex.Message)); }
		}

		#endregion

		#region Tool catalog

		private sealed class ToolInfo
		{
			public ToolInfo(Type type, string name, string tooltip)
			{
				Type	= type;
				Name	= name;
				Tooltip	= tooltip;
			}

			public Type		Type		{ get; private set; }
			public string	FullName	{ get { return Type.FullName; } }
			public string	Name		{ get; private set; }
			public string	Tooltip		{ get; private set; }

			public override string ToString() { return Name; }
		}

		// The catalog is scanned once per instance; compiling a new drawing tool reloads all scripts anyway.
		private Dictionary<string, ToolInfo> GetCatalog()
		{
			if (toolCatalog == null)
				toolCatalog = LoadCatalog();
			return toolCatalog;
		}

		// Every drawing tool that appears in the chart's drawing tools menu, keyed by full type name.
		private Dictionary<string, ToolInfo> LoadCatalog()
		{
			Dictionary<string, ToolInfo> catalog = new Dictionary<string, ToolInfo>(StringComparer.Ordinal);

			foreach (Type type in Core.Globals.AssemblyRegistry.GetDerivedTypes(typeof(DrawingTools.DrawingTool)))
			{
				if (type == null || type.IsAbstract || type.FullName == null || catalog.ContainsKey(type.FullName))
					continue;

				try
				{
					DrawingTools.DrawingTool tool = Activator.CreateInstance(type) as DrawingTools.DrawingTool;
					if (tool != null && tool.DisplayOnChartsMenus)
					{
						string name = string.IsNullOrWhiteSpace(tool.Name) ? type.Name : tool.Name;
						catalog[type.FullName] = new ToolInfo(type, name, string.IsNullOrWhiteSpace(tool.DisplayName) ? name : tool.DisplayName);
					}
				}
				catch (Exception ex)
				{
					Print(string.Format("{0}: skipped drawing tool {1} ({2})", Name, type.FullName, ex.Message));
				}
			}

			return catalog;
		}

		// The selected tools in their saved order. Unknown or duplicate entries are ignored.
		private List<ToolInfo> GetSelectedTools(Dictionary<string, ToolInfo> catalog)
		{
			List<ToolInfo>	result	= new List<ToolInfo>();
			HashSet<string>	seen	= new HashSet<string>(StringComparer.Ordinal);

			foreach (string raw in (ToolList ?? string.Empty).Split(ToolListSeparator))
			{
				string		name = raw.Trim();
				ToolInfo	tool;
				if (name.Length > 0 && seen.Add(name) && catalog.TryGetValue(name, out tool))
					result.Add(tool);
			}

			return result;
		}

		#endregion

		#region Configure tools window

		private void OpenToolConfigWindow()
		{
			Dictionary<string, ToolInfo>		catalog		= GetCatalog();
			ObservableCollection<ToolInfo>		selected	= new ObservableCollection<ToolInfo>(GetSelectedTools(catalog));
			ObservableCollection<ToolInfo>		available	= new ObservableCollection<ToolInfo>();

			void RefillAvailable()
			{
				available.Clear();
				foreach (ToolInfo t in catalog.Values.Where(t => !selected.Contains(t)).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
					available.Add(t);
			}
			RefillAvailable();

			// Fixed list height so long lists scroll instead of growing past the window.
			ListBox availableList	= new ListBox { ItemsSource = available, Height = 340, MinWidth = 180 };
			ListBox selectedList	= new ListBox { ItemsSource = selected, Height = 340, MinWidth = 180 };
			ScrollViewer.SetVerticalScrollBarVisibility(availableList, ScrollBarVisibility.Auto);
			ScrollViewer.SetVerticalScrollBarVisibility(selectedList, ScrollBarVisibility.Auto);
			ScrollViewer.SetCanContentScroll(availableList, true);
			ScrollViewer.SetCanContentScroll(selectedList, true);

			void AddTool()
			{
				ToolInfo t = availableList.SelectedItem as ToolInfo;
				if (t == null)
					return;
				int index = availableList.SelectedIndex;
				selected.Add(t);
				RefillAvailable();
				selectedList.SelectedItem	= t;
				availableList.SelectedIndex	= Math.Min(index, available.Count - 1);
			}

			void RemoveTool()
			{
				ToolInfo t = selectedList.SelectedItem as ToolInfo;
				if (t == null)
					return;
				int index = selectedList.SelectedIndex;
				selected.Remove(t);
				RefillAvailable();
				availableList.SelectedItem	= t;
				selectedList.SelectedIndex	= Math.Min(index, selected.Count - 1);
			}

			void MoveTool(int delta)
			{
				int from	= selectedList.SelectedIndex;
				int to		= from + delta;
				if (from < 0 || to < 0 || to >= selected.Count)
					return;
				selected.Move(from, to);
				selectedList.SelectedIndex = to;
				selectedList.ScrollIntoView(selectedList.SelectedItem);
			}

			availableList.MouseDoubleClick	+= (s, e) => AddTool();
			selectedList.MouseDoubleClick	+= (s, e) => RemoveTool();

			Button MakeButton(string text, Action onClick)
			{
				Button b = new Button { Content = text, Margin = new Thickness(4), Padding = new Thickness(10, 3, 10, 3), MinWidth = 90 };
				b.Click += (s, e) => onClick();
				return b;
			}

			StackPanel addRemove = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
			addRemove.Children.Add(MakeButton("Add >", AddTool));
			addRemove.Children.Add(MakeButton("< Remove", RemoveTool));

			StackPanel upDown = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
			upDown.Children.Add(MakeButton("Move up", () => MoveTool(-1)));
			upDown.Children.Add(MakeButton("Move down", () => MoveTool(1)));

			NinjaTrader.Gui.Tools.NTWindow window = new NinjaTrader.Gui.Tools.NTWindow
			{
				Caption					= "Configure drawing tools",
				Width					= 600,
				SizeToContent			= SizeToContent.Height,
				WindowStartupLocation	= WindowStartupLocation.CenterOwner,
				Owner					= Window.GetWindow(ChartControl)
			};

			StackPanel okCancel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
			okCancel.Children.Add(MakeButton("OK", () => window.DialogResult = true));
			okCancel.Children.Add(MakeButton("Cancel", () => window.DialogResult = false));

			Grid layout = new Grid { Margin = new Thickness(10) };
			layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

			void Place(UIElement element, int row, int column, int columnSpan = 1)
			{
				Grid.SetRow(element, row);
				Grid.SetColumn(element, column);
				Grid.SetColumnSpan(element, columnSpan);
				layout.Children.Add(element);
			}

			Place(new TextBlock { Text = "Available tools", Margin = new Thickness(0, 0, 0, 4) }, 0, 0);
			Place(new TextBlock { Text = "Shown on toolbar (in this order)", Margin = new Thickness(0, 0, 0, 4) }, 0, 2);
			Place(availableList, 1, 0);
			Place(addRemove, 1, 1);
			Place(selectedList, 1, 2);
			Place(upDown, 1, 3);
			Place(okCancel, 2, 0, 4);

			window.Content = layout;

			if (window.ShowDialog() == true)
			{
				ToolList = string.Join(ToolListSeparator.ToString(), selected.Select(t => t.FullName));
				RefreshTools();
			}
		}

		#endregion

		#region Resource helpers

		private static T Res<T>(string key, T fallback) where T : class
		{
			if (Application.Current == null)
				return fallback;
			return Application.Current.TryFindResource(key) as T ?? fallback;
		}

		private static double ResDouble(string key, double fallback)
		{
			object resource = Application.Current != null ? Application.Current.TryFindResource(key) : null;
			if (resource is double)
				return (double)resource;
			return fallback;
		}

		#endregion

		#region Properties

		[RefreshProperties(RefreshProperties.All)]
		[Display(Name = "Dock in chart toolbar", Description = "Show the toolbar in the chart window's toolbar instead of floating on the chart.", GroupName = "1. General", Order = 0)]
		public bool DockInToolbar { get; set; }

		[Range(0, 3000)]
		[Display(Name = "Toolbar offset (px)", Description = "Space to the left of the docked toolbar, to push it further right in the chart window's toolbar.", GroupName = "1. General", Order = 1)]
		public int ToolbarOffset { get; set; }

		[Range(1, 50)]
		[Display(Name = "Buttons per column", Description = "Number of tool buttons stacked in each column of the floating toolbar.", GroupName = "1. General", Order = 2)]
		public int NumberOfRows { get; set; }

		[Display(Name = "Visible only when chart is focused", Description = "Hide the floating toolbar while the chart window is not the active window.", GroupName = "1. General", Order = 3)]
		public bool IsVisibleOnlyFocused { get; set; }

		[Display(Name = "Show hide/show drawings button", Description = "Adds a button that hides, then restores, all hand-made drawings on this chart. Global drawings are not affected.", GroupName = "1. General", Order = 4)]
		public bool ShowHideToggleButton { get; set; }

		[Display(Name = "Show delete all drawings button", Description = "Adds a button that deletes all hand-made drawings on this chart after confirmation. Global and locked drawings are kept.", GroupName = "1. General", Order = 5)]
		public bool ShowDeleteAllButton { get; set; }

		// Saved hidden state, so a reloaded workspace hides the same drawings and the button shows the right icon.
		[Browsable(false)]
		public bool DrawingsHidden { get; set; }

		[Browsable(false)]
		public string HiddenDrawingTags { get; set; }

		// The tools on the toolbar as an ordered, ';'-separated list of full type names. Edited in the "Configure tools..." window.
		[Browsable(false)]
		public string ToolList { get; set; }

		[Browsable(false)]
		public double Left { get; set; }

		[Browsable(false)]
		public double Top { get; set; }

		#endregion
	}

	// Hides properties that don't apply to the toolbar, or to the current docking mode.
	public class icPADrawingToolbarConverter : IndicatorBaseConverter
	{
		private static readonly HashSet<string> AlwaysHidden = new HashSet<string>
		{
			"IsAutoScale", "DisplayInDataBox", "MaximumBarsLookBack", "Calculate",
			"PaintPriceMarkers", "Displacement", "ScaleJustification"
		};

		public override bool GetPropertiesSupported(ITypeDescriptorContext context)
		{
			return true;
		}

		public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext context, object component, Attribute[] attrs)
		{
			PropertyDescriptorCollection all = base.GetPropertiesSupported(context)
				? base.GetProperties(context, component, attrs)
				: TypeDescriptor.GetProperties(component, attrs);

			if (all == null)
				return null;

			icPADrawingToolbar	toolbar	= component as icPADrawingToolbar;
			bool				docked	= toolbar != null && toolbar.DockInToolbar;
			PropertyDescriptorCollection result = new PropertyDescriptorCollection(null);

			foreach (PropertyDescriptor pd in all)
			{
				if (!pd.IsBrowsable || AlwaysHidden.Contains(pd.Name))
					continue;
				if (docked && (pd.Name == nameof(icPADrawingToolbar.NumberOfRows) || pd.Name == nameof(icPADrawingToolbar.IsVisibleOnlyFocused)))
					continue;
				if (!docked && pd.Name == nameof(icPADrawingToolbar.ToolbarOffset))
					continue;

				result.Add(pd);
			}

			return result;
		}
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private icPA.icPADrawingToolbar[] cacheicPADrawingToolbar;
		public icPA.icPADrawingToolbar icPADrawingToolbar()
		{
			return icPADrawingToolbar(Input);
		}

		public icPA.icPADrawingToolbar icPADrawingToolbar(ISeries<double> input)
		{
			if (cacheicPADrawingToolbar != null)
				for (int idx = 0; idx < cacheicPADrawingToolbar.Length; idx++)
					if (cacheicPADrawingToolbar[idx] != null &&  cacheicPADrawingToolbar[idx].EqualsInput(input))
						return cacheicPADrawingToolbar[idx];
			return CacheIndicator<icPA.icPADrawingToolbar>(new icPA.icPADrawingToolbar(), input, ref cacheicPADrawingToolbar);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.icPA.icPADrawingToolbar icPADrawingToolbar()
		{
			return indicator.icPADrawingToolbar(Input);
		}

		public Indicators.icPA.icPADrawingToolbar icPADrawingToolbar(ISeries<double> input )
		{
			return indicator.icPADrawingToolbar(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.icPA.icPADrawingToolbar icPADrawingToolbar()
		{
			return indicator.icPADrawingToolbar(Input);
		}

		public Indicators.icPA.icPADrawingToolbar icPADrawingToolbar(ISeries<double> input )
		{
			return indicator.icPADrawingToolbar(input);
		}
	}
}

#endregion