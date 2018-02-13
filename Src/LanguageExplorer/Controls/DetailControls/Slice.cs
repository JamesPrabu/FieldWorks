// Copyright (c) 2005-2018 SIL International
// This software is licensed under the LGPL, version 2.1 or later
// (http://www.gnu.org/licenses/lgpl-2.1.html)

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;
using LanguageExplorer.Areas;
using SIL.LCModel.Core.Cellar;
using LanguageExplorer.Controls.DetailControls.Resources;
using LanguageExplorer.Controls.LexText;
using LanguageExplorer.Controls.XMLViews;
using LanguageExplorer.LcmUi;
using SIL.FieldWorks.Common.FwUtils;
using SIL.FieldWorks.Common.RootSites;
using SIL.FieldWorks.Resources;
using SIL.LCModel;
using SIL.LCModel.Infrastructure;
using SIL.LCModel.Utils;
using SIL.Xml;

namespace LanguageExplorer.Controls.DetailControls
{
	/// <summary>
	/// A Slice is essentially one row of a tree.
	/// It contains both a SliceTreeNode on the left of the splitter line, and a
	/// (optional) subclass of control on the right.
	/// </summary>
	/// <remarks>Slices know about drawing labels, measuring height, dealing with drag operations
	/// within the tree for this item, knowing whether the item can be expanded,
	/// and optionally drawing the part of the tree that is opposite the item, and
	/// many other things.}
	///</remarks>
	internal class Slice : UserControl, IFlexComponent
	{
#region Constants

		/// <summary>
		/// If label width is made wider than this, switch to full labels.
		/// </summary>
		const int MaxAbbrevWidth = 60;
		private const string Hotlinks = "hotlinks";
		private const string Menu = "menu";
		private const string always = "always";
		private const string ifdata = "ifdata";
		private const string never = "never";

		#endregion Constants

		#region Data members

		XElement m_configurationParameters;
		protected string m_strLabel;
		protected string m_strAbbr;
		protected bool m_isHighlighted = false;
		protected Font m_fontLabel = new Font(MiscUtils.StandardSansSerif, 10);
		protected Point m_location;
		protected ICmObject m_obj; // The object that will be the context if our children are expanded, or for figuring
		// what things can be inserted here.
		protected object[] m_key; // Key indicates path of nodes and objects used to construct this.
		protected LcmCache m_cache;
		// Indicates the 'weight' of object that starts at the top of this slice.
		// By default a slice is just considered to be a field (of the same object as the one before).
		protected ObjectWeight m_weight = ObjectWeight.field;
		protected bool m_widthHasBeenSetByDataTree;
		protected IPersistenceProvider m_persistenceProvider;
		private Dictionary<string, ToolStripMenuItem> m_visibilityMenus = new Dictionary<string, ToolStripMenuItem>();
		private SplitContainer m_splitContainer;

#endregion Data members

#region Properties

		/// <summary>
		/// The weight of object that starts at the beginning of this slice.
		/// </summary>
		public ObjectWeight Weight
		{
			get
			{
				CheckDisposed();

				return m_weight;
			}
			set
			{
				CheckDisposed();

				m_weight = value;
			}
		}

		internal string HotlinksMenuId => XmlUtils.GetOptionalAttributeValue(CallerNode ?? ConfigurationNode, Hotlinks, string.Empty);

		internal string OrdinaryMenuId => XmlUtils.GetOptionalAttributeValue(CallerNode ?? ConfigurationNode, Menu, string.Empty);

		/// <summary>
		/// Add these menus:
		/// 1. Separator (but only if there are already items in the ContextMenuStrip).
		/// 2. 'Field Visbility', and its three sub-menus.
		/// 3. Have Slice subclasses to add ones they need (e.g., Writing Systems and its sub-menus).
		/// 4. 'Help...'
		/// </summary>
		internal void AddCoreContextMenus(ref Tuple<ContextMenuStrip, CancelEventHandler, List<Tuple<ToolStripMenuItem, EventHandler>>> sliceTreeNodeContextMenuStripTuple)
		{
			ContextMenuStrip contextMenuStrip;
			List<Tuple<ToolStripMenuItem, EventHandler>> menuItems;
			if (sliceTreeNodeContextMenuStripTuple == null)
			{
				// Nobody added a context menu, we we have to do it.
				contextMenuStrip = new ContextMenuStrip();
				contextMenuStrip.Opening += TopLevelContextmenuOnOpening;
				menuItems = new List<Tuple<ToolStripMenuItem, EventHandler>>();
				sliceTreeNodeContextMenuStripTuple = new Tuple<ContextMenuStrip, CancelEventHandler, List<Tuple<ToolStripMenuItem, EventHandler>>>(contextMenuStrip, TopLevelContextmenuOnOpening, menuItems);
			}
			else
			{
				contextMenuStrip = sliceTreeNodeContextMenuStripTuple.Item1;
				menuItems = sliceTreeNodeContextMenuStripTuple.Item3;
			}
			if (contextMenuStrip.Items.Count > 0)
			{
				// 1. Add separator (since there are already items in the context menu).
				contextMenuStrip.Items.Add(new ToolStripSeparator());
			}
			// 2. 'Field Visbility', and its three sub-menus.
			var contextmenu = new ToolStripMenuItem(LanguageExplorerResources.ksFieldVisibility);
			contextMenuStrip.Items.Add(contextmenu);
			m_visibilityMenus.Add(always, ToolStripMenuItemFactory.CreateToolStripMenuItemForToolStripMenuItem(menuItems, contextmenu, AlwaysVisible_Clicked, LanguageExplorerResources.ksAlwaysVisible));
			m_visibilityMenus.Add(ifdata, ToolStripMenuItemFactory.CreateToolStripMenuItemForToolStripMenuItem(menuItems, contextmenu, IfDataVisibility_Click, LanguageExplorerResources.ksHiddenUnlessData));
			m_visibilityMenus.Add(never, ToolStripMenuItemFactory.CreateToolStripMenuItemForToolStripMenuItem(menuItems, contextmenu, NeverVisibility_Click, LanguageExplorerResources.ksNormallyHidden));

			// 3. Have Slice subclasses to add ones they need (e.g., Writing Systems and its sub-menus).
			AddSpecialContextMenus(contextMenuStrip, menuItems);

			// 4. 'Help...'
			ToolStripMenuItemFactory.CreateToolStripMenuItemForContextMenuStrip(menuItems, contextMenuStrip, Help_Clicked, LanguageExplorerResources.ksHelp, string.Empty, Keys.None, ResourceHelper.ButtonMenuHelpIcon);
		}

		private void Help_Clicked(object sender, EventArgs eventArgs)
		{
			ShowHelp.ShowHelpTopic(PropertyTable.GetValue<IHelpTopicProvider>("HelpTopicProvider"), GetSliceHelpTopicID());
		}

		private void AlwaysVisible_Clicked(object sender, EventArgs eventArgs)
		{
			SetFieldVisibility(always);
		}

		private void IfDataVisibility_Click(object sender, EventArgs eventArgs)
		{
			SetFieldVisibility(ifdata);
		}

		/// <summary />
		private void NeverVisibility_Click(object sender, EventArgs eventArgs)
		{
			SetFieldVisibility(never);
		}

		private void TopLevelContextmenuOnOpening(object sender, CancelEventArgs cancelEventArgs)
		{
			// Set checked state of the three visibility menus.
			foreach (var kvp in m_visibilityMenus)
			{
				kvp.Value.Checked = IsVisibilityItemChecked(kvp.Key);
				if (kvp.Value.Checked)
				{
					// Only one of the three is checked.
					break;
				}
			}
			PrepareToShowContextMenu();
		}

		protected virtual void PrepareToShowContextMenu()
		{ /* Nothing to do here. Suclasses can override and do more, if desired. */ }

		protected virtual void AddSpecialContextMenus(ContextMenuStrip topLevelContextMenuStrip, List<Tuple<ToolStripMenuItem, EventHandler>> menuItems)
		{ /* Nothing to do here either. Suclasses can override and add more, if desired. */ }

		/// <summary />
		public object[] Key
		{
			get
			{
				CheckDisposed();

				return m_key;
			}
			set
			{
				CheckDisposed();

				m_key = value;
			}
		}

		/// <summary />
		public IPersistenceProvider PersistenceProvider
		{
			get
			{
				CheckDisposed();
				return m_persistenceProvider;
			}

			set
			{
				CheckDisposed();
				m_persistenceProvider = value;
			}
		}

		/// <summary />
		public DataTree ContainingDataTree
		{
			get
			{
				CheckDisposed();

				return Parent as DataTree;
			}
		}
		protected internal SplitContainer SplitCont
		{
			get
			{
				CheckDisposed();

				return m_splitContainer;
			}
		}

		/// <summary />
		public SliceTreeNode TreeNode
		{
			get
			{
				CheckDisposed();

				return m_splitContainer.Panel1.Controls[0] as SliceTreeNode;
			}
		}

		/// <summary />
		public LcmCache Cache
		{
			get
			{
				CheckDisposed();

				return m_cache;
			}
			set
			{
				CheckDisposed();

				m_cache = value;
			}
		}

		/// <summary />
		public ICmObject Object
		{
			get
			{
				CheckDisposed();

				return m_obj;
			}
			set
			{
				CheckDisposed();

				m_obj = value;
			}
		}

		/// <summary>
		/// the XmlNode that was used to construct this slice
		/// </summary>
		public XElement ConfigurationNode { get; set; }

		/// <summary>
		/// This node stores the caller for future processing
		/// </summary>
		public XElement CallerNode { get; set; }

		// Review JohnT: or just make it public? Or make more delegation methods?
		/// <summary />
		public virtual Control Control
		{
			get
			{
				CheckDisposed();

				Debug.Assert(m_splitContainer.Panel2.Controls.Count == 0 || m_splitContainer.Panel2.Controls.Count == 1);

				return m_splitContainer.Panel2.Controls.Count == 1 ? m_splitContainer.Panel2.Controls[0] : null;
			}
			set
			{
				CheckDisposed();

				Debug.Assert(m_splitContainer.Panel2.Controls.Count == 0);

				if (value == null)
				{
					return;
				}

				m_splitContainer.Panel2.Controls.Add(value);
			}
		}

		/// <summary>
		/// this tells whether the slice should be treated as a handle on the owned atomic object which it
		/// refers to, for the purposes of deletion, copy, etc. For example, the Pronunciation field of
		/// LexVariant owns a LexPronunciation, so the Pronunciation field should have this attribute set.
		/// </summary>
		/// <example>MoEndoCompound has a "linker" slice which actually just wraps the attributes of a
		/// MoForm that is owned in the linker attribute. so when the user opens the context menu on this slice
		/// he really wants to be operating on the linker, not be owning MoEndoCompound.
		/// Another example. LexVariant owns an atomic LexPronunciation in a Pronunciation field. If we set
		/// wrapsAtomic for the Pronunciation field, then this returns true, allowing an Insert Pronunciation
		/// menu to activate.</example>
		public bool WrapsAtomic
		{
			get
			{
				CheckDisposed();

				return XmlUtils.GetOptionalBooleanAttributeValue(ConfigurationNode, "wrapsAtomic", false);
			}
		}

		/// <summary>
		/// is this node representing a property which is an (ordered) sequence?
		/// </summary>
		public bool IsSequenceNode
		{
			get
			{
				CheckDisposed();

				var node = ConfigurationNode?.Element("seq");
				if (node == null)
				{
					return false;
				}

				var field = XmlUtils.GetOptionalAttributeValue(node, "field");
				if (string.IsNullOrEmpty(field))
				{
					return false;
				}

				Debug.Assert(m_obj != null, "JH Made a false assumption!");
				var flid = GetFlid(field);
				Debug.Assert(flid != 0); // current field should have ID!
				//at this point we are not even thinking about showing reference sequences in the DataTree
				//so I have not dealt with that
				return GetFieldType(flid) == (int)CellarPropertyType.OwningSequence;
			}
		}

		/// <summary>
		/// is this node representing a property which is an (unordered) collection?
		/// </summary>
		public bool IsCollectionNode
		{
			get
			{
				CheckDisposed();

				if (ConfigurationNode == null)
				{
					return false;
				}
				return ConfigurationNode.Element("seq") != null && !IsSequenceNode;
			}
		}

		/// <summary>
		/// is this node a header?
		/// </summary>
		public bool IsHeaderNode
		{
			get
			{
				CheckDisposed();

				return XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "header") == "true";
			}
		}

		/// <summary>
		/// whether the label should be highlighted or not
		/// </summary>
		public bool Highlighted
		{
			set
			{
				CheckDisposed();

				var current = m_isHighlighted;
				m_isHighlighted = value;
				// See LT-5415 for how to get here with TreeNode == null, possibly while this
				// slice is being disposed in the call to the base class Dispose method.

				// If TreeNode is null, then this object has been disposed.
				// Since we now throw an exception, in the CheckDisposed method if it is disposed,
				// there is now no reason to ask if it is null.
				if (current != m_isHighlighted)
				{
					Refresh();
				}
			}
		}

		#endregion Properties

		#region Construction and initialization

		/// <summary />
		public Slice()
		{
			// Create a SplitContainer to hold the two (or one control.
			m_splitContainer = new SplitContainer
			{
				TabStop = false,
				AccessibleName = "Slice.SplitContainer",
				// Do this once right away, mainly so child controls like check box that don't control
				// their own height will get it right; then  after the controls get added to it, don't do it again
				// until our own size is definitely established by SetWidthForDataTreeLayout.
				Size = Size
			};
			Controls.Add(m_splitContainer);
			// This is really important. Since some slices are invisible, all must be,
			// or Show() will reorder them.
			Visible = false;
		}

		/// <summary />
		public Slice(Control ctrlT)
			: this()
		{
			Control = ctrlT;
#if _DEBUG
			Control.CheckForIllegalCrossThreadCalls = true;
#endif
		}

		/// <summary>
		/// This method should be called once the various properties of the slice have been set,
		/// particularly the Cache, Object, Key, and Spec. The slice may create its Control in
		/// this method, so don't assume it exists before this is called. It should be called
		/// before installing the slice.
		/// </summary>
		public virtual void FinishInit()
		{
			CheckDisposed();
		}

		protected override void OnEnter(EventArgs e)
		{
			CheckDisposed();

			base.OnEnter(e);

			if (ContainingDataTree == null || ContainingDataTree.ConstructingSlices) // FWNX-423, FWR-2508
			{
				return;
			}

			ContainingDataTree.CurrentSlice = this;

			TakeFocus(false);
		}

		#endregion Construction and initialization

		#region Miscellaneous UI methods

		/// <summary />
		public virtual void RegisterWithContextHelper()
		{
			CheckDisposed();

			if (Control != null)//grouping nodes do not have a control
			{
				//It's OK to send null as an id
				if (Publisher != null) // helpful for robustness and testing.
				{
#if RANDYTODO
					// TODO: Skip it for now, and figure out what to do with those context menus
					var caption = StringTable.Table.LocalizeAttributeValue(XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "label", ""));
					Publisher.Publish("RegisterHelpTargetWithId", new object[] { Control, caption, HelpId });
#endif
				}
			}
		}

		protected virtual string HelpId
		{
			get
			{
				CheckDisposed();

				//if the idea has not been added, try using the "field" attribute as the key
				return XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "id") ?? XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "field");
			}
		}

		/// <summary>
		/// This is passed the color that the XDE specified, if any, otherwise null.
		/// The default is to use the normal window color for editable text.
		/// Subclasses which know they should have a different default should
		/// override this method, but normally should use the specified color if not
		/// null.
		/// </summary>
		public virtual void OverrideBackColor(String backColorName)
		{
			CheckDisposed();

			if (Control == null)
			{
				return;
			}

			Control.BackColor = backColorName != null
				? (backColorName == "Control" ? Color.FromKnownColor(KnownColor.ControlLight) : Color.FromName(backColorName))
				: SystemColors.Window;
		}

		/// <summary>
		/// We tend to get a visual stuttering effect if sub-controls are made visible before the
		/// main slice is correctly positioned. This method is called after the slice is positioned
		/// to give it a chance to make embedded controls visible.
		/// This default implementation does nothing.
		/// </summary>
		public virtual void ShowSubControls()
		{
			CheckDisposed();
		}

		#endregion Miscellaneous UI methods

		#region events, clicking, etc.

		/// <summary />
		public void OnTreeNodeClick(object sender, EventArgs args)
		{
			CheckDisposed();

			TakeFocus();
		}

		/// <summary />
		public void TakeFocus()
		{
			CheckDisposed();

			TakeFocus(true);
		}

		/// <summary>
		/// The slice should become the focus slice (and return true).
		/// If the fOkToFocusTreeNode argument is false, this should happen iff it has a control which
		/// is appropriate to focus.
		/// Note: JohnT: recently I noticed that trying to focus the tree node doesn't seem to do
		/// anything; I'm not sure passing true is useful.
		/// </summary>
		public bool TakeFocus(bool fOkToFocusTreeNode)
		{
			CheckDisposed();

			var ctrl = Control;
			if (!Visible)
			{
				if ((ctrl != null && ctrl.TabStop) || fOkToFocusTreeNode)
				{
					// We very possibly want to focus this node, but .NET won't let us focus it till it is visible.
					// Make it so.
					DataTree.MakeSliceVisible(this);
				}
			}

			if (ctrl != null && ctrl.CanFocus && ctrl.TabStop)
			{
				ctrl.Focus();
			}
			else if (fOkToFocusTreeNode)
			{
				TreeNode.Focus();
			}
			else
			{
				return false;
			}

			//this is a bit of a hack, because focus and OnEnter are related but not equivalent...
			//some slices  never get an on enter, but  claim to be focus-able.
			if (ContainingDataTree.CurrentSlice != this)
			{
				ContainingDataTree.CurrentSlice = this;
			}
			return true;
		}

		/// <summary>
		/// Focus the main child control, if possible.
		/// </summary>
		protected override void OnGotFocus(EventArgs e)
		{
			CheckDisposed();
			if (Disposing)
			{
				return;
			}
			DataTree.MakeSliceVisible(this); // otherwise no change our control can take focus.
			base.OnGotFocus(e);
			if (Control != null && Control.CanFocus)
			{
				Control.Focus();
			}
		}

#endregion events, clicking, etc.

#region Tree management

		/// <summary>
		/// In some contexts we insert into the slice array a 'dummy' slice
		/// which can handle some queries directly (e.g., it may know
		/// its indentation level) but needs to 'BecomeReal' if it becomes
		/// fully visible. The purpose is laziness...often we insert the
		/// same dummy slice into many locations, and they are progressively
		/// replaced with real ones.
		/// </summary>
		public virtual bool IsRealSlice
		{
			get
			{
				CheckDisposed();

				return true;
			}
		}

		/// <summary>
		/// In some contexts, we use a "ghost" slice to represent data that
		/// has not yet been created.  These are "real" slices, but they don't
		/// represent "real" data.  Thus, for example, the underlying object
		/// can't be deleted because it doesn't exist.  (But the ghost slice
		/// may claim to have an object, because it needs such information to
		/// create the data once the user decides to type something...
		/// </summary>
		public virtual bool IsGhostSlice
		{
			get
			{
				CheckDisposed();

				return false;
			}
		}

		/// <summary>
		/// Some 'unreal' slices can become 'real' (ready to actually display) without
		/// actually replacing themselves with a different object. Such slices override
		/// this method to do whatever is needed and then answer true. If a slice
		/// answers false to IsRealSlice, this is tried, and if it returns false,
		/// then BecomeReal is called.
		/// </summary>
		public virtual bool BecomeRealInPlace()
		{
			CheckDisposed();

			return false;
		}

		/// <summary>
		/// In some contexts we insert into the slice array
		/// </summary>
		public virtual Slice BecomeReal(int index)
		{
			CheckDisposed();

			return this;
		}

		private void SetViewStylesheet(Control control, DataTree tc)
		{
			var rootSite = control as SimpleRootSite;
			if (rootSite != null && rootSite.StyleSheet == null)
			{
				rootSite.StyleSheet = tc.StyleSheet;
			}

			foreach (Control c in control.Controls)
			{
				SetViewStylesheet(c, tc);
			}
		}

		/// <summary></summary>
		public virtual bool ShowContextMenuIconInTreeNode()
		{
			CheckDisposed();

			return this == ContainingDataTree.CurrentSlice;
		}

		/// <summary></summary>
		public virtual void SetCurrentState(bool isCurrent)
		{
			CheckDisposed();

			if (Control is INotifyControlInCurrentSlice && !BeingDiscarded)
			{
				((INotifyControlInCurrentSlice)Control).SliceIsCurrent = isCurrent;
			}
			TreeNode?.Invalidate();

			var slice = this;
			while (slice != null && !slice.IsDisposed)
			{
				slice.Active = isCurrent;
				if (slice.IsHeaderNode)
				{
					break;
				}
				slice = slice.ParentSlice;
			}
		}
		protected SliceContextMenuFactory SliceContextMenuFactory { get; private set; }

		/// <summary />
		public virtual void Install(DataTree parentDataTree)
		{
			CheckDisposed();

			if (parentDataTree == null)
			{
				throw new InvalidOperationException("The slice '" + GetType().Name + "' must be placed in the Parent.Controls property before installing it.");
			}

			SliceContextMenuFactory = parentDataTree.SliceContextMenuFactory;

			m_splitContainer.SuspendLayout();
			// prevents the controls of the new 'SplitContainer' being NAMELESS
			if (m_splitContainer.Panel1.AccessibleName == null)
			{
				m_splitContainer.Panel1.AccessibleName = "Panel1";
			}

			if (m_splitContainer.Panel2.AccessibleName == null)
			{
				m_splitContainer.Panel2.AccessibleName = "Panel2";
			}

			SliceTreeNode treeNode;
			var isBeingReused = m_splitContainer.Panel1.Controls.Count > 0;
			if (isBeingReused)
			{
				treeNode = (SliceTreeNode)m_splitContainer.Panel1.Controls[0];
			}
			else
			{
				// Make a standard SliceTreeNode now.
				treeNode = new SliceTreeNode(this, SliceContextMenuFactory, XmlUtils.GetOptionalAttributeValue(CallerNode ?? ConfigurationNode, "menu", string.Empty));
				treeNode.SuspendLayout();
				treeNode.Dock = DockStyle.Fill;
				m_splitContainer.Panel1.Controls.Add(treeNode);
				m_splitContainer.AccessibleName = "SplitContainer";
			}

			if (!string.IsNullOrEmpty(Label))
			{
				// Susanna wanted to try five, rather than the default of four
				// to see if wider and still invisble made it easier to work with.
				// It may end up being made visible in a light grey color, but then it would
				// go back to the default of four.
				// Being visible at four may be too overpowering, so we may have to
				// manually draw a thin line to give the user a que as to where the splitter bar is.
				// Then, if it gets to be visible, we will probably need to add a bit of padding between
				// the line and the main slice content, or its text will be connected to the line.
				m_splitContainer.SplitterWidth = 5;

				// It was hard-coded to 40, but it isn't right for indented slices,
				// as they then can be shrunk so narrow as to completely cover up their label.
				m_splitContainer.Panel1MinSize = (20 * (Indent + 1)) + 20;
				m_splitContainer.Panel2MinSize = 0; // min size of right pane
				// This makes the splitter essentially invisible.
				m_splitContainer.BackColor = Color.FromKnownColor(KnownColor.Window); //to make it invisible
				treeNode.MouseEnter += TreeNodeMouseEnter;
				treeNode.MouseLeave += TreeNodeMouseLeave;
				treeNode.MouseHover += TreeNodeMouseEnter;
			}
			else
			{
				// SummarySlice is one of these kinds of Slices.
				//Debug.WriteLine("Slice gets no usable splitter: " + GetType().Name);
				m_splitContainer.SplitterWidth = 1;
				m_splitContainer.Panel1MinSize = LabelIndent();
				m_splitContainer.SplitterDistance = LabelIndent();
				m_splitContainer.IsSplitterFixed = true;
				// Just in case it was previously installed with a different label.
				treeNode.MouseEnter -= TreeNodeMouseEnter;
				treeNode.MouseLeave -= TreeNodeMouseLeave;
				treeNode.MouseHover -= TreeNodeMouseEnter;
			}

			int newHeight;
			var mainControl = Control;
			if (mainControl != null)
			{
				// Has SliceTreeNode and Control.
				// Set stylesheet on every view-based child control that doesn't already have one.
				SetViewStylesheet(mainControl, parentDataTree);
				mainControl.AccessibleName = string.IsNullOrEmpty(Label) ? "Slice_unknown" : Label;
				newHeight = Math.Max(mainControl.Height, LabelHeight);
				mainControl.Dock = DockStyle.Fill;
				m_splitContainer.FixedPanel = FixedPanel.Panel1;
			}
			else
			{
				// Has SliceTreeNode but no Control.
				newHeight = LabelHeight;
				m_splitContainer.Panel2Collapsed = true;
				m_splitContainer.FixedPanel = FixedPanel.Panel2;
			}

			if (!isBeingReused)
			{
				parentDataTree.Controls.Add(this); // Parent will have to move it into the right place.
				parentDataTree.Slices.Add(this);
			}
#if __MonoCS__ // FWNX-266
			if (mainControl != null && mainControl.Visible == false)
			{
				// ensure Launcher Control is shown.
				mainControl.Visible = true;
			}
#endif
			SetSplitPosition();

			// Don'f fire off all those size changed event handlers, unless it is really needed.
			if (Height != newHeight)
			{
				Height = newHeight;
			}
			treeNode.ResumeLayout(false);
			m_splitContainer.ResumeLayout();
		}

		private void TreeNodeMouseLeave(object sender, EventArgs e)
		{
			Highlighted = false;
		}

		private void TreeNodeMouseEnter(object sender, EventArgs e)
		{
			Highlighted = true;
		}

		/// <summary>
		/// Attempt to set the split position, but do NOT modify the global setting for
		/// the data tree if unsuccessful. This occurs during window initialization, since
		/// (I think) slices are created before the proper width is set for the containing
		/// data pane, and the constraints on the width of the splitter may not allow it to
		/// take on the persisted position.
		/// </summary>
		internal void SetSplitPosition()
		{
			Debug.Assert(m_splitContainer != null, "LT-13912 -- Need to determine why the SplitContainer is null here.");
			if (m_splitContainer == null || m_splitContainer.IsSplitterFixed) // LT-13912 apparently sc comes out null sometimes.
			{
				return;
			}

			var valueSansLabelindent = ContainingDataTree.SliceSplitPositionBase;
			var correctSplitPosition = valueSansLabelindent + LabelIndent();
			if (m_splitContainer.SplitterDistance == correctSplitPosition)
			{
				return;
			}
			m_splitContainer.SplitterDistance = correctSplitPosition;
			TreeNode.Invalidate();
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			CheckDisposed();

			// Skip handling this, if the DataTree hasn't
			// set the official width using SetWidthForDataTreeLayout
			if (!m_widthHasBeenSetByDataTree)
			{
				return;
			}

			// LT-18750 Calling OnSizeChanged in the base class sometimes resets the AutoScrollPosition to the top of the Slice (Windows).
			// When m_splitter.Size is changed, it also has the same effect. It is possible that ScrollControlIntoView() is called
			// in a method subscribed to an event in the base class, but my investigation was unsuccessful.
			var oldPoint = ContainingDataTree.AutoScrollPosition;
			var scrollChanged = false;
			base.OnSizeChanged(e);
			if (ContainingDataTree.AutoScrollPosition != oldPoint)
			{
				scrollChanged = true;
			}
			int verticalAdjustment = Size.Height - m_splitContainer.Size.Height;
			// This should be done by setting DockStyle to Fill but that somehow doesn't always fix the
			// height of the splitter's panels.
			m_splitContainer.Size = Size;
			if (scrollChanged)
			{
				var verticalScrollChangedCorrectly = ContainingDataTree.AutoScrollPosition.Y - oldPoint.Y == -verticalAdjustment;
				if (!verticalScrollChangedCorrectly)
				{
					// Set the AutoScrollPosition to scroll the distance reflecting the change to the SplitContainer
					ContainingDataTree.AutoScrollPosition = new Point(-oldPoint.X, -oldPoint.Y + verticalAdjustment);
				}
			}
			// This definitely seems as if it shouldn't be necessary at all. And if it were necessary,
			// it should be fine to just call PerformLayout at once. But it doesn't work. Dragging the splitter
			// of a multi-line view in a way that changes its height somehow leaves the panels of the splitter
			// a different height from the splitter itself, which means that when making it narrower,
			// and therefore higher, the bottom of the longer view is cut off. This is the only workaround
			// that I (JohnT) have been able to find. It's possible that something has layout suspended
			// (since this can get called from OnSizeChanged of a child window, I think) and it only
			// works to layout the splitter afterwards? Anyway this seems to work...test carefully if you
			// think of taking it out.
			if (m_splitContainer.Panel2.Height != Height)
			{
				Application.Idle += LayoutSplitter;
			}
		}

		private void LayoutSplitter(object sender, EventArgs e)
		{
			Application.Idle -= LayoutSplitter;
			if (m_splitContainer != null && !IsDisposed)
			{
				m_splitContainer.PerformLayout();
			}
		}

		/// <summary>
		/// If we don't have a splitter (because no label), set the width of the
		/// tree node directly; the other node's size is set by being docked 'fill'.
		/// </summary>
		protected override void OnLayout(LayoutEventArgs levent)
		{
			CheckDisposed();

			if (m_splitContainer.Panel2Collapsed)
			{
				TreeNode.Width = LabelIndent();
			}

			base.OnLayout(levent);
		}

		/// <summary>
		/// Indicates whether this is an active slice, which means it displays extra
		/// controls. Currently only SummarySlices can be active.
		/// (We could use this for the context menu icon, but that only shows on
		/// the actual current slice, whereas several slices may show commands.)
		/// </summary>
		public virtual bool Active
		{
			get
			{
				CheckDisposed();

				return false;
			}
			set
			{
				CheckDisposed();
			}
		}

		#region IDisposable override

		/// <summary />
		public void CheckDisposed()
		{
			if (IsDisposed)
			{
				throw new ObjectDisposedException(ToString() + GetHashCode(), "Trying to use object that has been disposed.");
			}
		}

		/// <summary>
		/// Executes in two distinct scenarios.
		///
		/// 1. If disposing is true, the method has been called directly
		/// or indirectly by a user's code via the Dispose method.
		/// Both managed and unmanaged resources can be disposed.
		///
		/// 2. If disposing is false, the method has been called by the
		/// runtime from inside the finalizer and you should not reference (access)
		/// other managed objects, as they already have been garbage collected.
		/// Only unmanaged resources can be disposed.
		/// </summary>
		/// <remarks>
		/// If any exceptions are thrown, that is fine.
		/// If the method is being done in a finalizer, it will be ignored.
		/// If it is thrown by client code calling Dispose,
		/// it needs to be handled by fixing the bug.
		///
		/// If subclasses override this method, they should call the base implementation.
		/// </remarks>
		protected override void Dispose(bool disposing)
		{
			Debug.WriteLineIf(!disposing, "****************** Missing Dispose() call for " + GetType().Name + ". ******************");
			// Must not be run more than once.
			if (IsDisposed)
			{
				return;
			}
			if (Disposing)
			{
				return; // Should throw, to let us know to use DestroyHandle, before calling base method.
			}

			if (disposing)
			{
				var parent = Parent as DataTree;
				parent?.RemoveDisposedSlice(this);

				// Dispose managed resources here.
				m_splitContainer.SplitterMoved -= mySplitterMoved;
				// If anyone but the owning DataTree called this to be disposed,
				// then it will still hold a referecne to this slice in an event handler.
				// We could take care of it here by asking the DT to remove it,
				// but I (RandyR) am inclined to not do that, since
				// only the DT is really authorized to dispose its slices.
				m_fontLabel?.Dispose();
			}

			// Dispose unmanaged resources here, whether disposing is true or false.
			m_fontLabel = null;
			m_cache = null;
			m_key = null;
			m_obj = null;
			CallerNode = null;
			ConfigurationNode = null;
			m_configurationParameters = null;
			m_strLabel = null;
			m_strAbbr = null;
			ParentSlice = null;
			PropertyTable = null;
			Publisher = null;
			Subscriber = null;

			base.Dispose(disposing);
		}

#endregion IDisposable override

		/// <summary>
		/// This method determines how much we should indent nodes produced from "part ref"
		/// elements embedded inside an "indent" element in another "part ref" element.
		/// Currently, by default we in fact do NOT add any indent, unless there is also
		/// an attribute indent="true".
		/// </summary>
		/// <returns>0 for no indent, 1 to indent.</returns>
		internal static int ExtraIndent(XElement indentNode)
		{
			return XmlUtils.GetOptionalBooleanAttributeValue(indentNode, "indent", false) ? 1 : 0;
		}

		/// <summary></summary>
		public virtual void GenerateChildren(XElement node, XElement caller, ICmObject obj, int indent,
			ref int insPos, ArrayList path, ObjSeqHashMap reuseMap, bool fUsePersistentExpansion)
		{
			CheckDisposed();

			// If node has children, figure what to do with them...
			// XmlNodeList children = node.ChildNodes; // unused variable
			NodeTestResult ntr;
			// We may get child nodes from either the node itself or the calling part, but currently
			// don't try to handle both; we consider the children of the caller, if any, to override
			// the children of the node (but not unify with them, since a different kind of children
			// are involved).
			// A newly created slice is always in state ktisFixed, but that is not appropriate if it
			// has children from either source. However, a node which notionally has children may in fact have nothing to
			// show, perhaps because a sequence is empty. First evaluate this, and if true, set it
			// to ktisCollapsedEmpty.
			//bool fUseChildrenOfNode;
			XElement indentNode = null;
			if (caller != null)
			{
				indentNode = caller.Element("indent");
			}
			if (indentNode != null)
			{
				// Similarly pretest for children of caller, to see whether anything is produced.
				ContainingDataTree.ApplyLayout(obj, this, indentNode, indent + ExtraIndent(indentNode), insPos, path, reuseMap, true, out ntr);
			}
			else
			{
				var insPosT = insPos; // don't modify the real one in this test call.
				ntr = ContainingDataTree.ProcessPartChildren(node, path, reuseMap, obj, this, indent + ExtraIndent(node), ref insPosT, true, null, false, node);
			}

			switch (ntr)
			{
				case NodeTestResult.kntrNothing:
					Expansion = TreeItemState.ktisFixed; // probably redundant, but play safe
					break;
				case NodeTestResult.kntrPossible:
					// It could have children but currently can't: we always show this as collapsedEmpty.
					Expansion = TreeItemState.ktisCollapsedEmpty;
					break;
				default:
					if (Expansion == TreeItemState.ktisCollapsed)
					{
						// Reusing a node that was collapsed (and it has something to expand):
						// leave it that way (whatever the spec says).
					}
					else
					{
						// It has children: decide whether to expand them.
						// Old code does not expand by default, couple of ways to override.
						var fExpand = XmlUtils.GetOptionalAttributeValue(node, "expansion") != "doNotExpand";
						if (fUsePersistentExpansion)
						{
							Expansion = TreeItemState.ktisCollapsed; // Needs to be an expandable state to have ExpansionStateKey.
							fExpand = PropertyTable.GetValue(ExpansionStateKey, fExpand);
						}
						if (fExpand)
						{
							// Record the expansion state and generate the children.
							Expansion = TreeItemState.ktisExpanded;
							CreateIndentedNodes(caller, obj, indent, ref insPos, path, reuseMap, node);
						}
						else
						{
							// Record expansion state and skip generating children.
							Expansion = TreeItemState.ktisCollapsed;
						}
					}

					break;
			}
		}

		/// <summary />
		public virtual void CreateIndentedNodes(XElement caller, ICmObject obj, int indent, ref int insPos, ArrayList path, ObjSeqHashMap reuseMap, XElement node)
		{
			CheckDisposed();

			string parameter = null;
			if (caller != null)
			{
				parameter = XmlUtils.GetOptionalAttributeValue(caller, "param");
			}
			XElement indentNode = null;
			if (caller != null)
			{
				indentNode = caller.Element("indent");
			}
			if (indentNode != null)
			{
				NodeTestResult ntr;
				insPos = ContainingDataTree.ApplyLayout(obj, this, indentNode, indent + ExtraIndent(indentNode), insPos, path, reuseMap, false, out ntr);
			}
			else
			{
				ContainingDataTree.ProcessPartChildren(node, path, reuseMap, obj, this, indent + ExtraIndent(node), ref insPos, false, parameter, false, caller);
			}
		}

#endregion Tree management

#region Tree Display

		// Delegation methods (mainly or entirely duplicate similar methods on embedded control).
		/// <summary></summary>
		public virtual int LabelHeight
		{
			get
			{
				CheckDisposed();
				return m_fontLabel.Height;
			}
		}

		/// <summary>
		/// Determines how deeply indented this item is in the tree diagram. 0 means no indent.
		/// </summary>
		public int Indent { get; set; }

		/// <summary>
		/// Return the expansion state of tree nodes.
		/// </summary>
		/// <returns>A tree state enum.</returns>
		public virtual TreeItemState Expansion { get; set; } = TreeItemState.ktisFixed;

		/// <summary>
		/// Gets and sets the label used to identify the item in the tree diagram.
		/// May need to override if not using the standard variable to draw a simple label
		/// </summary>
		public virtual string Label
		{
			get
			{
				CheckDisposed();

				return m_strLabel;
			}
			set
			{
				CheckDisposed();

				m_strLabel = value;
			}
		}

		/// <summary />
		public string Abbreviation
		{
			get
			{
				CheckDisposed();

				return m_strAbbr;
			}
			set
			{
				CheckDisposed();

				m_strAbbr = value;
				if (string.IsNullOrEmpty(m_strAbbr) && m_strLabel != null)
				{
					var len = m_strLabel.Length > 4 ? 4 : m_strLabel.Length;
					m_strAbbr = m_strLabel.Substring(0, len);
				}
			}
		}

		/// <summary>
		/// Text to display as tooltip for label (SliceTreeNode).
		/// Defaults to Label.
		/// </summary>
		public string ToolTip
		{
			get
			{
				CheckDisposed();

				return StringTable.Table.LocalizeAttributeValue(XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "tooltip", Label));
			}
		}

		/// <summary>
		/// Help Topic ID for the slice
		/// </summary>
		public string HelpTopicID
		{
			get
			{
				CheckDisposed();

				return XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "helpTopicID");
			}
		}

		/// <summary>
		/// Help Topic ID for the slice
		/// </summary>
		public string ChooserDlgHelpTopicID
		{
			get
			{
				CheckDisposed();

				return XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "chooserDlgHelpTopicID");
			}
		}

		/// <summary />
		public string GetSliceHelpTopicID()
		{
			return GetHelpTopicID(HelpTopicID, "khtpField");
		}

		/// <summary />
		public string GetChooserHelpTopicID()
		{
			return GetHelpTopicID(ChooserDlgHelpTopicID, "khtpChoose");
		}

		public string GetChooserHelpTopicID(string ChooserDlgHelpTopicID)
		{
			return GetHelpTopicID(ChooserDlgHelpTopicID, "khtpChoose");
		}

		private string GetHelpTopicID(string xmlHelpTopicID, string generatedIDPrefix)
		{
			string helpTopicID;

			if (xmlHelpTopicID == "khtpField-PhRegularRule-RuleFormula")
			{
				xmlHelpTopicID = "khtpChoose-Environment";
			}

			return !string.IsNullOrEmpty(xmlHelpTopicID) ? xmlHelpTopicID : GenerateHelpTopicId(generatedIDPrefix);
		}

		private string GenerateHelpTopicId(string helpTopicPrefix)
		{
			string generatedHelpTopicID;
			var tempfieldName = XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "field");
			var templabelName = XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "label");
			var areaChoice = PropertyTable.GetValue<string>(AreaServices.AreaChoice);
			var toolChoice = PropertyTable.GetValue<string>(AreaServices.ToolChoice);
			var parentHvo = Convert.ToInt32(XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "hvoDisplayParent"));

			if (tempfieldName == "Targets" && parentHvo != 0)
			{
				// Cross Reference (entry level) or lexical relation (sense level) subitems
				var repo = m_cache.ServiceLocator.GetInstance<ILexEntryRepository>();
				ILexEntry lex;
				repo.TryGetObject(parentHvo, out lex);

				if (lex != null) // It must be the entry level
				{
					generatedHelpTopicID = helpTopicPrefix + "-" + toolChoice + "-CrossReferenceSubitem";
				}
				else // It must be the sense level
				{
					generatedHelpTopicID = helpTopicPrefix + "-" + toolChoice + "-LexicalRelationSubitem";

				}
			}
			else
			{
				templabelName = getAlphaNumeric(templabelName);
				if (string.IsNullOrEmpty(tempfieldName))
				{
					// try to use the slice label, without spaces.
					tempfieldName = templabelName;
				}
				generatedHelpTopicID = GetGeneratedHelpTopicId(helpTopicPrefix, tempfieldName);
				if (helpTopicIsValid(generatedHelpTopicID))
				{
					return generatedHelpTopicID;
				}
				// try to use the slice label, without spaces if the helpTopicID does not work for the field xml attribute.
				generatedHelpTopicID = GetGeneratedHelpTopicId(helpTopicPrefix, templabelName);
				if (helpTopicIsValid(generatedHelpTopicID))
				{
					return generatedHelpTopicID;
				}
				if (helpTopicPrefix.Equals("khtpChoose"))
				{
					generatedHelpTopicID = "khtpChoose-CmPossibility";
				}
				else if (areaChoice == AreaServices.ListsAreaMachineName)
				{
					generatedHelpTopicID = "khtp-CustomListField"; // If the list isn't defined, use the generic list help topic

				}
				else
				{
					generatedHelpTopicID = "khtpNoHelpTopic"; // else use the generic no help topic
				}
			}

			return generatedHelpTopicID;
		}

		private string GetGeneratedHelpTopicId(string helpTopicPrefix, string fieldName)
		{
			var ownerClassName = Object.Owner?.ClassName;
			var className = Cache.DomainDataByFlid.MetaDataCache.GetClassName(Object.ClassID);
			// Distinguish the Example (sense) field and the expanded example (LexExtendedNote) field
			className = (fieldName == "Example" && ownerClassName == "LexExtendedNote") ? "LexExtendedNote" : className;
			// Distinguish the Translation (sense) field and the expanded example (LexExtendedNote) field
			className = fieldName.StartsWith("Translation")&& (ownerClassName == "LexExtendedNote" || (Object.Owner != null && Object.Owner.ClassName == "LexExtendedNote")) ? "LexExtendedNote" : className;
			var toolChoice = PropertyTable.GetValue<string>(AreaServices.ToolChoice);

			var generatedHelpTopicID = helpTopicPrefix + "-" + toolChoice + "-" + className + "-" + fieldName;

			if (helpTopicIsValid(generatedHelpTopicID))
			{
				return generatedHelpTopicID;
			}
			if (string.Equals(className, "CmPossibility"))
			{
				generatedHelpTopicID = helpTopicPrefix + "-" + toolChoice + "-" + Object.SortKey + "-" + fieldName;
			}
			if (helpTopicIsValid(generatedHelpTopicID))
			{
				return generatedHelpTopicID;
			}
			generatedHelpTopicID = helpTopicPrefix + "-" + toolChoice + "-" + fieldName;
			if (helpTopicIsValid(generatedHelpTopicID))
			{
				return generatedHelpTopicID;
			}
			generatedHelpTopicID = helpTopicPrefix + "-" + className + "-" + fieldName;
			if (!helpTopicIsValid(generatedHelpTopicID))
			{
				generatedHelpTopicID = helpTopicPrefix + "-" + fieldName;
			}
			return generatedHelpTopicID;
		}

		/// <summary>
		/// Generates a possible help topic id from the field name, but does NOT check it for validity!
		/// </summary>
		private static string getAlphaNumeric(string fromStr)
		{
			var candidateID = new StringBuilder(string.Empty);

			if (string.IsNullOrEmpty(fromStr))
			{
				return candidateID.ToString();
			}

			// Should we capitalize the next letter?
			var nextCapital = true;

			// Lets turn our field into a candidate help page!
			foreach (var ch in fromStr)
			{
				if (char.IsLetterOrDigit(ch)) // might we include numbers someday?
				{
					candidateID.Append(nextCapital ? char.ToUpper(ch) : ch);
					nextCapital = false;
				}
				else // unrecognized character... exclude it
				{
					nextCapital = true; // next letter should be a capital
				}
			}
			return candidateID.ToString();
		}

		/// <summary>
		/// Is m_helpTopic a valid help topic?
		/// </summary>
		private bool helpTopicIsValid(string helpStr)
		{
			if (string.IsNullOrEmpty(helpStr))
			{
				return false;
			}
			var actualHelpString = PropertyTable.GetValue<IHelpTopicProvider>("HelpTopicProvider").GetHelpString(helpStr);
			if (actualHelpString == "NullStringId")
			{
				actualHelpString = null;
			}
			return !string.IsNullOrEmpty(actualHelpString);
		}

		/// <summary />
		private void DrawLabel(int x, int y, Graphics gr, int clipWidth)
		{
			CheckDisposed();

			Image image = null;
			if (IsSequenceNode)
			{
				image = DetailControlsStrings.SequenceSmall;
			}
			else if (IsCollectionNode)
			{
				image = DetailControlsStrings.CollectionNode;
			}
			else if (IsObjectNode || WrapsAtomic)
			{
				image = DetailControlsStrings.atomicNode;
			}
			if (image != null)
			{
				((Bitmap)image).MakeTransparent(Color.Fuchsia);
				gr.DrawImage(image, x, y);
				x += image.Width;
			}
			var p = new PointF(x, y);
			using (Brush brush = new SolidBrush(Color.FromKnownColor(KnownColor.ControlDarkDark)))
			{
				var label = Label;
				if (m_splitContainer.SplitterDistance <= MaxAbbrevWidth)
				{
					label = Abbreviation;
				}
				gr.DrawString(label, m_fontLabel, brush, p);
			}
		}

		/// <summary>
		/// Returns the height, from the top of the item, at which to draw the line across towards it.
		/// Typically this is the center of where DrawLabel will draw the label, but it might not be (e.g.,
		/// if DrawLabel actually draws two labels and a bit of tree diagram).
		/// </summary>
		public virtual int GetBranchHeight()
		{
			CheckDisposed();

			return Convert.ToInt32((m_fontLabel.GetHeight() + 1.0) / 2.0);
		}

		/// <summary />
		public void Expand()
		{
			CheckDisposed();

			Expand(IndexInContainer);
		}

		/// <summary />
		public int IndexInContainer
		{
			get
			{
				CheckDisposed();

				return ContainingDataTree.Slices.IndexOf(this);
			}
		}

		/// <summary>
		/// Get a key suitable to use in the PropertyTable to specify whether this slice should be
		/// expanded or not. This will be null if it can't be expanded, since we don't need to persist
		/// that. Otherwise we currently base it on the Guid of the Object.
		/// </summary>
		internal string ExpansionStateKey
		{
			get
			{
				if (Expansion == TreeItemState.ktisFixed || Expansion == TreeItemState.ktisCollapsedEmpty)
				{
					return null; // nothing useful to remember
				}
				if (Object == null)
				{
					return null; // not sure this can happen, but without a key we can't do anything useful.
				}
				return "expand" + Convert.ToBase64String(Object.Guid.ToByteArray());
			}
		}

		/// <summary>
		/// Expand this node, which is at position iSlice in its parent.
		/// </summary>
		/// <remarks> I (JH) don't know why this was written to take the index of the slice.
		/// It's just as easy for this class to find its own index.
		/// JohnT: for performance; finding its own index is a linear search,
		/// and the caller often has the info already, especially in loops expanding many children.</remarks>
		public virtual void Expand(int iSlice)
		{
			CheckDisposed();

			try
			{
				ContainingDataTree.DeepSuspendLayout();
				XElement caller = null;
				if (Key.Length > 1)
				{
					caller = Key[Key.Length - 2] as XElement;
				}
				var insPos = iSlice + 1;
				CreateIndentedNodes(caller, m_obj, Indent, ref insPos, new ArrayList(Key), new ObjSeqHashMap(), ConfigurationNode);

				Expansion = TreeItemState.ktisExpanded;
				PropertyTable.SetProperty(ExpansionStateKey, true, true, true);
			}
			finally
			{
				ContainingDataTree.DeepResumeLayout();
				ContainingDataTree.EnsureDefaultCursorForSlices();
			}
		}

		private bool IsDescendant(Slice slice)
		{
			var parentSlice = slice.ParentSlice;
			while (parentSlice != null)
			{
				if (parentSlice == this)
				{
					return true;
				}
				parentSlice = parentSlice.ParentSlice;
			}
			return false;
		}

		/// <summary />
		public void Collapse()
		{
			CheckDisposed();

			Collapse(IndexInContainer);
		}

		/// <summary>
		/// Collapse this node, which is at position iSlice in its parent.
		/// </summary>
		public virtual void Collapse(int iSlice)
		{
			CheckDisposed();

			var iNextSliceNotChild = iSlice + 1;
			while (iNextSliceNotChild < ContainingDataTree.Slices.Count && IsDescendant(ContainingDataTree.FieldOrDummyAt(iNextSliceNotChild)))
			{
				iNextSliceNotChild++;
			}
			var count = iNextSliceNotChild - iSlice - 1;
			try
			{
				ContainingDataTree.DeepSuspendLayout();
				while (count > 0)
				{
					ContainingDataTree.RemoveSliceAt(iSlice + 1);
					count--;
				}
				Expansion = TreeItemState.ktisCollapsed;
				PropertyTable.SetProperty(ExpansionStateKey, false, true, true);
			}
			finally
			{
				ContainingDataTree.DeepResumeLayout();
			}
		}

		/// <summary>
		/// Record the slice that 'owns' this one (typically this was created by a CreateIndentedNodes call on the
		/// parent slice).
		/// </summary>
		public Slice ParentSlice { get; set; }

		/// <summary></summary>
		public virtual bool HandleMouseDown(Point p)
		{
			CheckDisposed();

			ContainingDataTree.CurrentSlice = this;
			return true;
		}

		/// <summary />
		public virtual int LabelIndent()
		{
			CheckDisposed();

			return SliceTreeNode.kdxpLeftMargin + (Indent + 1) * SliceTreeNode.kdxpIndDist;
		}

		/// <summary>
		/// Draws the label in the containing SilTreeControl's Graphics object at the specified position.
		/// Override if you have a more complex type of label, e.g., if the field contains interlinear
		/// data and you want to label each line.
		/// </summary>
		internal void DrawLabel(int y, Graphics gr, int clipWidth)
		{
			CheckDisposed();

			DrawLabel(LabelIndent(), y, gr, clipWidth);
		}

		/// <summary></summary>
		public bool IsObjectNode
		{
			get
			{
				CheckDisposed();

				var sClassName = Object.ClassName;
				return
					(ConfigurationNode.Element("node") != null || ("PhCode" == sClassName))	// todo: this should tell if the attr (not the nested one) is to a basic type or a cmobject
					&& ConfigurationNode.Element("seq") == null &&
					//MoAlloAdhocProhib.adjacency is the top-level node, but it's not really an object that you should be able to delete
					Object != ContainingDataTree.Root;
			}
		}

#endregion Tree Display

#region Miscellaneous data methods

		/// <summary>
		/// Get the context for this slice considered as a member of a sequence.
		/// That is, if the current node is, at some level, a member of an owning sequence,
		/// find the most local such sequence, and return information indicating the position
		/// of the object this slice is part of, as well as the object and property that owns
		/// it.
		/// </summary>
		/// <param name="hvoOwner">Owner of the object this slice is part of.</param>
		/// <param name="flid">Owning sequence property this is part of.</param>
		/// <param name="ihvoPosition">Position of this object in owning sequence;
		/// or current position in cache, if a collection.</param>
		/// <returns>true if this slice is part of an owning sequence property.</returns>
		public bool GetSeqContext(out int hvoOwner, out int flid, out int ihvoPosition)
		{
			CheckDisposed();

			hvoOwner = 0; // compiler insists it be assigned.
			flid = 0;
			ihvoPosition = 0;

			if (m_key == null)
			{
				return false;
			}

			var cache = ContainingDataTree.Cache;
			var mdc = cache.DomainDataByFlid.MetaDataCache as IFwMetaDataCacheManaged;
			var repo = cache.ServiceLocator.GetInstance<ICmObjectRepository>();
			for (var inode = m_key.Length; --inode >= 0; )
			{
				var objNode = m_key[inode];
				var element = objNode as XElement;
				if (element == null || element.Name != "seq")
				{
					continue;
				}
				var attrName = element.Attribute("field").Value;
				int clsid;
				// if this is the last index, we don't have an hvo of anything to edit.
				// But it may be ghost slice that we can use.  (See FWR-556.)
				if (inode == m_key.Length - 1)
				{
					if (Object == null || element.Attribute("ghost") == null)
					{
						return false;
					}
					clsid = Object.ClassID;
					flid = ContainingDataTree.GetFlidIfPossible(clsid, attrName, mdc);
					if (flid == 0)
					{
						return false;
					}
					hvoOwner = Object.Hvo;
					ihvoPosition = -1;		// 1 before actual location.
					return true;
				}

				// got it!
				// The next thing we push into key right after the "seq" node is always the
				// HVO of the particular item we're editing.
				var hvoItem = (int)(m_key[inode + 1]);
				var obj = repo.GetObject(hvoItem);
				clsid = obj.Owner.ClassID;
				flid = ContainingDataTree.GetFlidIfPossible(clsid, attrName, mdc);
				if (flid == 0)
				{
					return false;
				}
				hvoOwner = obj.Owner.Hvo;
				ihvoPosition = cache.DomainDataByFlid.GetObjIndex(hvoOwner, flid, hvoItem);
				return true;
			}
			return false;
		}

		/// <summary>
		/// Get the context for this slice considered as part of an owning atomic attr.
		/// That is, if the current node is, at some level, a member of an owning atomic attr,
		/// find the most local such attr, and return information indicating the object
		/// and property that owns it.
		/// </summary>
		/// <param name="hvoOwner">Owner of the object this slice is part of.</param>
		/// <param name="flid">Owning atomic property this is part of.</param>
		/// <returns>true if this slice is part of an owning atomic property.</returns>
		public bool GetAtomicContext(out int hvoOwner, out int flid)
		{
			CheckDisposed();

			// Compiler requires values to be set, but these are meaningless.
			hvoOwner = 0;
			flid = 0;
			if (m_key == null)
			{
				return false;
			}

			for (var inode = m_key.Length; --inode >= 0; )
			{
				var objNode = m_key[inode];
				var element = objNode as XElement;
				if (element == null || element.Name != "atomic")
				{
					continue;
				}
				// got it!
				// The next thing we push into key right after the "atomic" node is always the
				// HVO of the particular item we're editing.
				var hvoItem = (int)(m_key[inode + 1]);
				var attrName = element.Attribute("field").Value;
				var cache = ContainingDataTree.Cache;
				flid = cache.DomainDataByFlid.MetaDataCache.GetFieldId2(cache.ServiceLocator.GetInstance<ICmObjectRepository>().GetObject(hvoItem).Owner.ClassID, attrName, true);
				return true;
			}
			return false;
		}

		/// <summary>
		/// Get the flid for the specified field name of this slice's object. May return zero if
		/// the object does not have that field.
		/// </summary>
		protected int GetFlid(string fieldName)
		{
			CheckDisposed();

			return ContainingDataTree.GetFlidIfPossible(m_obj.ClassID, fieldName, m_cache.DomainDataByFlid.MetaDataCache as IFwMetaDataCacheManaged);
		}

		protected int GetFieldType(int flid)
		{
			CheckDisposed();

			return m_cache.DomainDataByFlid.MetaDataCache.GetFieldType(flid);
		}

#endregion Miscellaneous data methods

#region Menu Command Handlers

		/// <summary>
		/// Answer whether clidTest is, or is a subclass of, clidSig.
		/// That is, either clidTest is the same as clidSig, or one of the base classes of clidTest is clidSig.
		/// As a special case, if clidSig is 0, all classes are considered to match
		/// </summary>
		private bool IsOrInheritsFrom(int clidTest, int clidSig)
		{
			CheckDisposed();

			return Cache.ClassIsOrInheritsFrom(clidTest, clidSig);
		}

		protected virtual bool ShouldHide => true;

		/// <summary>
		/// do an insertion
		/// </summary>
		/// <param name="fieldName">name of field to create in</param>
		/// <param name="className">class of object to create</param>
		/// <param name="ownerClassName">class of expected owner. If the current slice's object is not
		/// this class (or a subclass), look for a containing object that is.</param>
		/// <remarks> called by the containing environment in response to a user command.</remarks>
		internal void HandleInsertCommand(string fieldName, string className, string ownerClassName)
		{
			CheckDisposed();

			var newObjectClassId = m_cache.DomainDataByFlid.MetaDataCache.GetClassId(className);
			if (newObjectClassId == 0)
			{
				throw new ArgumentException($"There does not appear to be a database class named '{className}'.");
			}

			var ownerClassId = 0;
			if (!string.IsNullOrEmpty(ownerClassName))
			{
				ownerClassId = Cache.DomainDataByFlid.MetaDataCache.GetClassId(ownerClassName);
				if (ownerClassId == 0)
				{
					throw new ArgumentException($"There does not appear to be a database class named '{ownerClassName}'.");
				}
			}
			// Hiding this slice triggers any OnLeave() processing.  This is needed to prevent possibly
			// trying to create an undoable unit of work during PropChanged handling, as inserting a slice
			// due to inserting an object would otherwise trigger OnLeave() for this slice.  See FWR-1731.
			// But, for FWR-1107 the Hide call can cause all sorts of trouble with the sandbox in the Words-Analysis tool.
			if (ShouldHide)
			{
				Hide();
			}
			// First see whether THIS slice can do it. This helps us insert in the right position for things like
			// subsenses.
			if (InsertObjectIfPossible(newObjectClassId, ownerClassId, fieldName, this))
			{
				return;
			}
			// The previous call may have done the insert, but failed to recognize it due to disposing of the slice
			// during a PropChanged operation.  See LT-9005.
			if (IsDisposed)
			{
				return;
			}

			// See if any direct ancestor can do it.
			var index = IndexInContainer;
			for (var i = index - 1; i >= 0; i--)
			{
				if (InsertObjectIfPossible(newObjectClassId, ownerClassId, fieldName, ContainingDataTree.Slices[i]))
				{
					return;
				}
			}

			// Loop through all slices until we find a slice whose object is of the right class
			// and that has the specified field.
			foreach (var slice in ContainingDataTree.Slices)
			{
				Debug.WriteLine($"HandleInsertCommand({fieldName}, {className}, {ownerClassName ?? "nullOwner"}) -- slice = {slice}");
				if (InsertObjectIfPossible(newObjectClassId, ownerClassId, fieldName, slice))
				{
					break;
				}
			}
		}

		/// <returns>
		/// 'true' means we found a suitable place to insert an object,
		/// not that it was actually inserted. It may, or may not, have been inserted in this case.
		/// 'false' means no suitable place was found, so the calling code can try other locations.
		/// </returns>
		private bool InsertObjectIfPossible(int newObjectClassId, int ownerClassId, string fieldName, Slice slice)
		{
			if ((ownerClassId <= 0 || !IsOrInheritsFrom((slice.Object.ClassID), ownerClassId)) && slice.Object != Object && !slice.Object.Equals(ContainingDataTree.Root))
			{
				return false;
			}
			// The slice's object has an acceptable type provided it implements the required field.
			// See if the current slice's object has the field named.
			var flid = slice.GetFlid(fieldName);
			var mdc = Cache.MetaDataCacheAccessor as IFwMetaDataCacheManaged;
			var flidT = ContainingDataTree.GetFlidIfPossible(ownerClassId, fieldName, mdc);
			if (flidT != 0 && flid != flidT)
			{
				flid = flidT;
			}

			if (flid == 0)
			{
				return false;
			}
			// Found a suitable slice. Do the insertion.
			int insertionPosition;		// causes return false if not changed.
			if (m_cache.IsReferenceProperty(flid))
			{
				insertionPosition = InsertObjectIntoVirtualBackref(Cache, slice.Object.Hvo, newObjectClassId, flid);
			}
			else
			{
				insertionPosition = slice.InsertObject(flid, newObjectClassId);
			}
			if (insertionPosition < 0)
			{
				return insertionPosition == -2;		// -2 keeps dlg for adding subPOSes from firing for each slice when cancelled.
			}

			return true;
		}

		internal int InsertObjectIntoVirtualBackref(LcmCache cache, int hvoSlice, int clidNewObj, int flid)
		{
			var metadata = cache.ServiceLocator.GetInstance<IFwMetaDataCacheManaged>();
			if (!metadata.get_IsVirtual(flid))
			{
				return -1;
			}
			var sliceObj = cache.ServiceLocator.GetInstance<ICmObjectRepository>().GetObject(hvoSlice);
			var clidSlice = sliceObj.ClassID;
			if (clidNewObj != LexEntryTags.kClassId || clidSlice != LexEntryTags.kClassId)
			{
				return -1;
			}
			if (metadata.GetFieldName(flid) != "VariantFormEntryBackRefs")
			{
				return -1;
			}
			using (var dlg = new InsertVariantDlg())
			{
				dlg.InitializeFlexComponent(new FlexComponentParameters(PropertyTable, Publisher, Subscriber));
				var entOld = (ILexEntry) sliceObj;
				dlg.SetHelpTopic("khtpInsertVariantDlg");
				dlg.SetDlgInfo(cache, entOld);
				if (dlg.ShowDialog() == DialogResult.OK && dlg.NewlyCreatedVariantEntryRefResult)
				{
					return entOld.VariantFormEntryBackRefs.Count();
				}
				// say we've handled this.
				return -2;
			}
		}

		/// <summary>
		/// Insert a new object of the specified class into the specified property of your object.
		/// </summary>
		/// <returns>-1 if unsuccessful -2 if unsuccessful and no further attempts should be made,
		/// otherwise, index of new object (0 if collection)</returns>
		private int InsertObject(int flid, int newObjectClassId)
		{
			CheckDisposed();

			var fAbstract = m_cache.DomainDataByFlid.MetaDataCache.GetAbstract(newObjectClassId);
			if (fAbstract)
			{
				// We've been handed an abstract class to insert.  Try to determine the desired
				// concrete from the context.
				if (newObjectClassId == MoFormTags.kClassId && Object is ILexEntry)
				{
					var entry = ((ILexEntry)Object);
					newObjectClassId = entry.GetDefaultClassForNewAllomorph();
				}
				else
				{
					return -1;
				}
			}
			// OK, we can add to property flid of the object of slice slice.
			var insertionPosition = 0;//leave it at 0 if it does not matter
			var hvoOwner = Object.Hvo;
			var clidOwner = Object.ClassID;
			var clidOfFlid = flid / 1000;
			if (clidOwner != clidOfFlid && clidOfFlid == Object.Owner.ClassID)
			{
				hvoOwner = Object.Owner.Hvo;
			}
#if RANDYTODO
			// TODO: Can't Flex create a new object in a collection property?
#endif
			var mdcManaged = Cache.GetManagedMetaDataCache();
			var owningSeqFields = mdcManaged.GetFields(Object.ClassID, true, (int)CellarPropertyType.OwningSequence).ToList();
			if (!owningSeqFields.Contains(flid))
			{
				return -1;
			}
			insertionPosition = Cache.DomainDataByFlid.get_VecSize(hvoOwner, flid);

			if (ContainingDataTree.CurrentSlice != null)
			{
				var sda = m_cache.DomainDataByFlid;
				var chvo = insertionPosition;
				// See if the current slice in any way indicates a position in that property.
				var key = ContainingDataTree.CurrentSlice.Key;
				var fGotIt = false;
				for (var ikey = key.Length - 1; ikey >= 0 && !fGotIt; ikey--)
				{
					if (!(key[ikey] is int))
					{
						continue;
					}
					var hvoTarget = (int)key[ikey];
					for (var i = 0; i < chvo; i++)
					{
						if (hvoTarget == sda.get_VecItem(hvoOwner, flid, i))
						{
							insertionPosition = i + 1; // insert after current object.
							fGotIt = true; // break outer loop
							break;
						}
					}
				}
			}
			// Save DataTree for the finally block.  Note premature return below due to IsDisposed.  See LT-9005.
			var dtContainer = ContainingDataTree;
			try
			{
				dtContainer.SetCurrentObjectFlids(hvoOwner, flid);
				var fieldType = (CellarPropertyType) m_cache.MetaDataCacheAccessor.GetFieldType(flid);
				switch (fieldType)
				{
					case CellarPropertyType.OwningCollection:
						insertionPosition = -1;
						break;

					case CellarPropertyType.OwningAtomic:
						insertionPosition = -2;
						break;
				}
				using (var uiObj = CmObjectUi.CreateNewUiObject(PropertyTable, Publisher, newObjectClassId, hvoOwner, flid, insertionPosition))
				{
					// If uiObj is null, typically CreateNewUiObject displayed a dialog and the user cancelled.
					// We return -1 to make the caller give up trying to insert, so we don't get another dialog if
					// there is another slice that could insert this kind of object.
					if (uiObj == null)
					{
						return -2; // Nothing created.
					}
					// If 'this' isDisposed, typically the inserted object occupies a place in the record list for
					// this view, and inserting an object caused the list to be refreshed and all slices for this
					// record to be disposed. In that case, we won't be able to find a child of this to activate,
					// so we'll just settle for having created the object.
					// Enhance JohnT: possibly we could load information from the slice into local variables before
					// calling CreateNewUiObject so that we could do a better job of picking the slice to focus
					// after an insert which disposes 'this'. Or perhaps we could improve the refresh list process
					// so that it more successfully restores the current item without disposing of all the slices.
					if (IsDisposed)
					{
						return -1;
					}

					uiObj.InitializeFlexComponent(new FlexComponentParameters(PropertyTable, Publisher, Subscriber));

					switch (fieldType)
					{
						case CellarPropertyType.OwningCollection:
							// order is not fully predicatable, figure where it DID show up.
							insertionPosition = m_cache.DomainDataByFlid.GetObjIndex(hvoOwner, flid, uiObj.Object.Hvo);
							break;

						case CellarPropertyType.OwningAtomic:
							insertionPosition = 0;
							break;
					}
					if (hvoOwner == Object.Hvo && Expansion == TreeItemState.ktisCollapsed)
					{
						// We added something to the object of the current slice...almost certainly it
						// will be something that will display under this node...if it is still collapsed,
						// expand it to show the thing inserted.
						TreeNode.ToggleExpansion(IndexInContainer);
					}
					var child = ExpandSubItem(uiObj.Object.Hvo);
					child?.FocusSliceOrChild();
				}
			}
			finally
			{
				dtContainer.ClearCurrentObjectFlids();
			}
			return insertionPosition;
		}

		/// <summary>
		/// Find a slice nested below this one whose object is hvo and expand it if it is collapsed.
		/// </summary>
		/// <returns>Slice for subitem, or null</returns>
		public Slice ExpandSubItem(int hvo)
		{
			CheckDisposed();

			var cslice = ContainingDataTree.Slices.Count;
			for (var islice = IndexInContainer + 1; islice < cslice; ++islice)
			{
				var slice = ContainingDataTree.Slices[islice];
				if (slice.Object.Hvo == hvo)
				{
					if (slice.Expansion == TreeItemState.ktisCollapsed)
					{
						slice.TreeNode.ToggleExpansion(islice);
					}
					return slice;
				}
				// Stop if we get past the children of the current object.
				if (slice.Indent <= Indent)
				{
					break;
				}
			}
			return null;
		}


		/// <summary>
		/// Return true if the target array starts with the objects in the match array.
		/// </summary>
		internal static bool StartsWith(object[] target, object[] match)
		{
			if (match.Length > target.Length)
			{
				return false;
			}
			for (var i = 0; i < match.Length; i++)
			{
				var x = target[i];
				var y = match[i];
				// We need this special expression because two objects wrapping the same integer
				// are, pathologically, not equal to each other.
				if (x != y && !(x is int && y is int && (int)x == (int)y))
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>
		/// Focus the specified slice (or the first of its children that can accept focus).
		/// </summary>
		public Slice FocusSliceOrChild()
		{
			CheckDisposed();

			// Make sure that preceding slices are real and visible.  Otherwise, the
			// inserted slice can be shown in the wrong place.  See LT-6306.
			var iLastRealVisible = 0;
			for (var i = IndexInContainer - 1; i >= 0; --i)
			{
				var slice = ContainingDataTree.FieldOrDummyAt(i);
				if (slice.IsRealSlice && slice.Visible)
				{
					iLastRealVisible = i;
					break;
				}
			}
			// Be very careful...the call to FieldAt in this loop may dispose this!
			// Therefore almost any method call to this hereafter may crash.
			var containingDT = ContainingDataTree;
			var myIndex = IndexInContainer;
			var myKey = Key;
			for (var i = iLastRealVisible + 1; i < IndexInContainer; ++i)
			{
				var slice = containingDT.FieldAt(i);	// make it real.
				if (!slice.Visible) // make it visible.
				{
					DataTree.MakeSliceVisible(slice);
				}
			}
			var cslice = containingDT.Slices.Count;
			Slice sliceRetVal = null;
			for (var islice = myIndex; islice < cslice; ++islice)
			{
				var slice = containingDT.FieldAt(islice);
				DataTree.MakeSliceVisible(slice); // otherwise it can't take focus
				if (slice.TakeFocus(false))
				{
					sliceRetVal = slice;
					break;
				}
				// Stop if we get past the children of the current object.
				if (!StartsWith(slice.Key, myKey))
				{
					break;
				}
			}
			if (sliceRetVal != null)
			{
				var xDataTreeHeight = containingDT.Height;
				var ptScrollPos = containingDT.AutoScrollPosition;
				var delta = (xDataTreeHeight / 4) - sliceRetVal.Location.Y;
				if (delta < 0)
				{
					containingDT.AutoScrollPosition = new Point(-ptScrollPos.X, -ptScrollPos.Y - delta);
				}
			}
			return sliceRetVal;
		}

		/// <summary>
		/// Main work of deleting an object; answer true if it was actually deleted.
		/// </summary>
		internal bool HandleDeleteCommand()
		{
			CheckDisposed();

			var obj = GetObjectForMenusToOperateOn();
			// Build a list of neighboring slices, ordered by proximity (max of 40 either way...don't want to build too much
			// of a lazy view).
			var dataTree = ContainingDataTree;
			var nearbySlices = GetNearbySlices();
			bool result;
			if (obj == null)
			{
				throw new FwConfigurationException("Slice:GetObjectHvoForMenusToOperateOn is either messed up or should not have been called, because it could not find the object to be deleted.", ConfigurationNode);
			}
			try
			{
				dataTree.SetCurrentObjectFlids(obj.Hvo, 0);
				using (var ui = CmObjectUi.MakeUi(m_cache, obj.Hvo))
				{
					ui.InitializeFlexComponent(new FlexComponentParameters(PropertyTable, Publisher, Subscriber));
					result = ui.DeleteUnderlyingObject();
				}
			}
			finally
			{
				dataTree.ClearCurrentObjectFlids();
			}
			dataTree.SelectFirstPossibleSlice(nearbySlices);
			// The slice will likely be disposed in the DeleteUnderlyingObject call,
			// so make sure we aren't collected until we leave this method, at least.
			// MSDN docs: "This method (GC.KeepAlive) references the obj parameter, making that object ineligible for
			//	garbage collection from the start of the routine to the point, in execution order,
			//	where this method is called. Code this method at the end, not the beginning,
			//	of the range of instructions where obj must be available."
			GC.KeepAlive(this);
			return result;
		}

		/// <summary>
		/// Build a list of slices close to the current one, ordered by distance,
		/// starting with the slice itself. An arbitrary maximum distance (currently 40) is imposed,
		/// to minimize the time spent getting and using these; usually one of the first few is used.
		/// </summary>
		internal List<Slice> GetNearbySlices()
		{
			var index = IndexInContainer;
			var closeSlices = new List<Slice> {this};
			var count = ContainingDataTree.Slices.Count;
			var limit = Math.Min(Math.Max(index, count - index), 40);
			for (var i = 1; i <= limit; i++)
			{
				if (index - i >= 0)
				{
					closeSlices.Add(ContainingDataTree.FieldOrDummyAt(index - i));
				}

				if (index + i < count)
				{
					closeSlices.Add(ContainingDataTree.FieldOrDummyAt(index + i));
				}
			}
			return closeSlices;
		}

#if RANDYTODO
		/// <summary>
		/// Check whether a "Delete Reference" command can be executed.  Currently implemented
		/// only for the VariantEntryBackRefs / LexEntry/EntryRefs/ComponentLexemes references.
		/// </summary>
		public virtual bool CanDeleteReferenceNow(Command cmd)
		{
			CheckDisposed();
			return FromVariantBackRefField;
		}

		/// <summary>
		/// Handle a "Delete Reference" command.  Currently implemented only for the
		/// VariantEntryBackRefs / LexEntry/EntryRefs/ComponentLexemes references.
		/// </summary>
		public virtual void HandleDeleteReferenceCommand(Command cmd)
		{
			CheckDisposed();
			if (NextSlice != null && NextSlice.Object != null)
			{
				var ler = NextSlice.Object as ILexEntryRef;
				if (ler != null)
				{
					UndoableUnitOfWorkHelper.Do(DetailControlsStrings.ksUndoDeleteRef, DetailControlsStrings.ksRedoDeleteRef, ler, () =>
					{
						ler.ComponentLexemesRS.Remove(ContainingDataTree.Root);
						// probably not needed, but safe...
						if (ler.PrimaryLexemesRS.Contains(ContainingDataTree.Root))
							ler.PrimaryLexemesRS.Remove(ContainingDataTree.Root);
					});
				}
			}
		}
#endif

		/// <summary>
		/// Gives the object that should be the target of Delete, copy, etc. for menus operating on this slice label.
		/// </summary>
		/// <returns>Return null if this slice is supposed to operate on an atomic field which is currently empty.</returns>
		public ICmObject GetObjectForMenusToOperateOn()
		{
			CheckDisposed();

			if (WrapsAtomic)
			{
				var nodes = ConfigurationNode.Elements("atomic").ToList();
				if (nodes.Count != 1)
				{
					throw new FwConfigurationException("Expected to find a single <atomic> element in here", ConfigurationNode);
				}
				var field = XmlUtils.GetMandatoryAttributeValue(nodes[0], "field");
				var flid = GetFlid(field);
				Debug.Assert(flid != 0);
				var hvo = m_cache.DomainDataByFlid.get_ObjectProp(m_obj.Hvo, flid);
				return hvo != 0 ? m_cache.ServiceLocator.GetObject(hvo) : null;
			}
			return FromVariantBackRefField ? BackRefObject : m_obj;
		}

		/// <summary>
		/// Get the flid associated with this slice, if there is one.
		/// </summary>
		public virtual int Flid
		{
			get
			{
				if (ConfigurationNode == null || m_obj == null)
				{
					return 0;
				}
				var sField = XmlUtils.GetOptionalAttributeValue(ConfigurationNode, "field");
				return !string.IsNullOrEmpty(sField) ? GetFlid(sField) : 0;
			}
		}

		private bool FromVariantBackRefField
		{
			get
			{
				var rootObj = ContainingDataTree.Root;
				var clidRoot = rootObj.ClassID;
				return clidRoot == LexEntryTags.kClassId &&
					Object != null && Object != rootObj &&
					Object.Owner != null && Object.Owner != rootObj &&
					(Object.ClassID == clidRoot || Object.Owner.ClassID == clidRoot);
			}
		}

		private ICmObject BackRefObject
		{
			get
			{
				var rootObj = ContainingDataTree.Root;
				var clidRoot = rootObj.ClassID;
				if (clidRoot == LexEntryTags.kClassId &&
					Object != null && Object != rootObj &&
					Object.Owner != null && Object.Owner != rootObj)
				{
					if (Object.ClassID == clidRoot)
					{
						return Object;
					}

					if (Object.Owner.ClassID == clidRoot)
					{
						return Object.Owner;
					}
				}
				return null;
			}
		}

		/// <summary>
		/// is it possible to do a deletion menu command on this slice right now?
		/// </summary>
		public bool GetCanDeleteNow()
		{
			CheckDisposed();

			var obj = GetObjectForMenusToOperateOn();
			if (obj == null)
			{
				return false;
			}

			var owner = obj.Owner;
			if (owner == null) // We can allow unowned objects to be deleted.
			{
				return true;
			}
			var flid = obj.OwningFlid;
			if (!owner.IsFieldRequired(flid))
			{
				return true;
			}

			//now, if the field is required, then we do not allow this to be deleted if it is atomic
			//futureTodo: this prevents the user from the deleting something in order to create something
			//of a different class, or to paste in other object in this field.
			if (!Cache.IsVectorProperty(flid))
			{
				return false;
			}

			// still OK to delete so long as it is not the last item.
			return Cache.DomainDataByFlid.get_VecSize(owner.Hvo, flid) > 1;
		}

		/// <summary>
		/// Is it possible to do a merge menu command on this slice right now?
		/// </summary>
		public bool GetCanMergeNow()
		{
			CheckDisposed();

			var obj = GetObjectForMenusToOperateOn();
			if (obj == null)
			{
				return false;
			}

			var owner = obj.Owner;
			var flid = obj.OwningFlid;
			// No support yet for atomic properties.
			if (!Cache.IsVectorProperty(flid))
			{
				return false;
			}

			// Special handling for allomorphs, as they can be merged into the lexeme form.
			var clsid = obj.ClassID;
			switch (flid)
			{
				case LexEntryTags.kflidAlternateForms:
					// We can merge an alternate with the lexeme form,
					// if it is the same class.
					if (clsid == ((ILexEntry) owner).LexemeFormOA.ClassID)
					{
						return true;
					}

					break;
				case LexSenseTags.kflidSenses:
					return true;
			}
			// A subsense can always merge into its owning sense.
			var vectorSize = Cache.DomainDataByFlid.get_VecSize(owner.Hvo, flid);
			if (owner.IsFieldRequired(flid) && vectorSize < 2)
			{
				return false;
			}

			// Check now to see if there are any other objects of the same class in the flid,
			// since only objects of the same class can be merged.
			int[] contents;
			var chvoMax = Cache.DomainDataByFlid.get_VecSize(owner.Hvo, flid);
			using (var arrayPtr = MarshalEx.ArrayToNative<int>(chvoMax))
			{
				Cache.DomainDataByFlid.VecProp(owner.Hvo, flid, chvoMax, out chvoMax, arrayPtr);
				contents = MarshalEx.NativeToArray<int>(arrayPtr, chvoMax);
			}
			foreach (var hvoInner in contents)
			{
				var innerObj = m_cache.ServiceLocator.GetInstance<ICmObjectRepository>().GetObject(hvoInner);
				if (innerObj != obj && clsid == innerObj.ClassID)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary />
		public virtual void HandleMergeCommand(bool fLoseNoTextData)
		{
			CheckDisposed();

			var obj = GetObjectForMenusToOperateOn();
			if (obj == null)
			{
				throw new FwConfigurationException("Slice:GetObjectHvoForMenusToOperateOn is either messed up or should not have been called, because it could not find the object to be merged.", ConfigurationNode);
			}

			using (var ui = CmObjectUi.MakeUi(m_cache, obj.Hvo))
			{
				ui.InitializeFlexComponent(new FlexComponentParameters(PropertyTable, Publisher, Subscriber));
				ui.MergeUnderlyingObject(fLoseNoTextData);
			}
			// The slice will likely be disposed in the MergeUnderlyingObject call,
			// so make sure we aren't collected until we leave this method, at least.
			GC.KeepAlive(this);
		}

		/// <summary>
		/// Is it possible to do a split menu command on this slice right now?
		/// </summary>
		public bool GetCanSplitNow()
		{
			CheckDisposed();

			var obj = GetObjectForMenusToOperateOn();
			if (obj == null)
			{
				return false;
			}

			var owner = obj.Owner;
			var flid = obj.OwningFlid;
			if (!Cache.IsVectorProperty(flid))
			{
				return false;
			}

			// For example, a LexSense belonging to a LexSense can always be split off to a new
			// LexEntry.
			var clsid = obj.ClassID;
			if (clsid == owner.ClassID)
			{
				return true;
			}

			// Otherwise, we need at least two vector items to be able to split off this one.
			return Cache.DomainDataByFlid.get_VecSize(owner.Hvo, flid) >= 2;
		}

		protected override void OnValidating(System.ComponentModel.CancelEventArgs e)
		{
			base.OnValidating(e);
			// Some slices do something validation-like on loss of focus; occasionally we want
			// to force it to happen before then.
			OnLostFocus(new EventArgs());
		}

		/// <summary></summary>
		public virtual void HandleSplitCommand()
		{
			CheckDisposed();

			var obj = GetObjectForMenusToOperateOn();
			if (obj == null)
			{
				throw new FwConfigurationException("Slice:GetObjectHvoForMenusToOperateOn is either messed up or should not have been called, because it could not find the object to be moved to a copy of its owner.", ConfigurationNode);
			}

			using (var ui = CmObjectUi.MakeUi(m_cache, obj.Hvo))
			{
				ui.InitializeFlexComponent(new FlexComponentParameters(PropertyTable, Publisher, Subscriber));
				ui.MoveUnderlyingObjectToCopyOfOwner();
			}
			// The slice will likely be disposed in the MoveUnderlyingObjectToCopyOfOwner call,
			// so make sure we aren't collected until we leave this method, at least.
			GC.KeepAlive(this);
		}

		/// <summary></summary>
		public virtual void HandleCopyCommand(Slice newSlice, string label)
		{
			CheckDisposed();

			var origObj = GetObjectForMenusToOperateOn();
			if (origObj == null)
			{
				throw new FwConfigurationException("OriginalSlice:GetObjectHvoForMenusToOperateOn is either messed up or should not have been called, because it could not find the object to be moved to a copy of its owner.", ConfigurationNode);
			}

			var newObj = newSlice.GetObjectForMenusToOperateOn();
			if (newObj == null)
			{
				throw new FwConfigurationException("NewSlice:GetObjectHvoForMenusToOperateOn is either messed up or should not have been called, because it could not find the object to be moved to a copy of its owner.", ConfigurationNode);
			}

			if (origObj is ICloneableCmObject)
			{
				var undoMsg = $"Undo {label}";
				var redoMsg = $"Redo {label}";
				UndoableUnitOfWorkHelper.Do(undoMsg, redoMsg, m_cache.ActionHandlerAccessor, () => { ((ICloneableCmObject)origObj).SetCloneProperties(newObj); });
			}
			else
			{
				throw new NotSupportedException($"{origObj.ClassName} is not set up for copying!");
			}
		}

		/// <summary />
		public virtual void HandleEditCommand()
		{
			CheckDisposed();
		}

		/// <summary>
		/// This was added for Lexical Relation slices which now have the Add/Replace Reference menu item in
		/// the dropdown menu.
		/// </summary>
		public virtual void HandleLaunchChooser()
		{
			CheckDisposed();

			// Implemented as needed by subclasses.
		}

		/// <summary />
		public virtual bool GetCanEditNow()
		{
			CheckDisposed();

			return true;
		}

#endregion Menu Command Handlers

		/// <summary>
		/// Updates the display of a slice, if an hvo and tag it cares about has changed in some way.
		/// </summary>
		/// <returns>true, if it the slice updated its display</returns>
		protected internal virtual bool UpdateDisplayIfNeeded(int hvo, int tag)
		{
			CheckDisposed();

			return false;
		}

		protected void SetFieldVisibility(string visibility)
		{
			CheckDisposed();

			if (IsVisibilityItemChecked(visibility))
			{
				return; // No change, so skip a lot of trauma.
			}

			ReplacePartWithNewAttribute("visibility", visibility);
			var dt = ContainingDataTree;
			if (!dt.ShowingAllFields)
			{
				dt.RefreshList(true);
#if RANDYTODO
			// TODO: I wonder how long that 'Temporary block' has lasted? (A: 26 Jan 2007.)
#endif
				// Temporary block. It isn't selecting the right one,
				// and it ends up reorganizing the slices, if 'this' was the Pronunciation field
				// and is no longer visible.
				//if (!dt.GotoNextSliceAfterIndex(islice - 1)) // ideally select at SAME index.
				//	dt.GotoPreviousSliceBeforeIndex(islice);
			}
		}

		protected void ReplacePartWithNewAttribute(string attr, string attrValueNew)
		{
			XElement newPartref;
			var newLayout = Inventory.MakeOverride(Key, attr, attrValueNew, LayoutCache.LayoutVersionNumber, out newPartref);
			Inventory.GetInventory("layouts", m_cache.ProjectId.Name).PersistOverrideElement(newLayout);
			var dataTree = ContainingDataTree;
			var rootKey = Key[0] as XElement;
			// The first item in the key is always the root XML node for the whole display. This has now changed,
			// so if we don't do something, subsequent visibility commands for other slices will use the old
			// version as a basis and lose the change we just made (unless we Refresh, which we don't want to do
			// when showing everything). Also, if we do refresh, we'll discard and remake everything.
			foreach (var slice in dataTree.Slices.Where(slice => slice.Key != null && slice.Key.Length >= 0 && slice.Key[0] == rootKey && rootKey != newLayout))
			{
				slice.Key[0] = newLayout;
			}

			int lastPartRef;
			var oldPartRef = PartRef(out lastPartRef);
			if (oldPartRef == null)
			{
				return;
			}

			oldPartRef = (XElement)Key[lastPartRef];
			Key[lastPartRef] = newPartref;
			// Loop skips dummy slices, which have a null 'Key' (LT-5817).
			foreach (var slice in dataTree.Slices.Where(slice => slice.Key != null))
			{
				for (var i = 0; i < slice.Key.Length; i++)
				{
					var node = slice.Key[i] as XElement;
					if (node == null)
					{
						continue;
					}

					if (XmlUtils.NodesMatch(oldPartRef, node))
					{
						slice.Key[i] = newPartref;
					}
				}
			}
		}

		/// <summary>
		/// extract the "part ref" node from the slice.Key
		/// </summary>
		protected internal XElement PartRef()
		{
			int indexInKey;
			return PartRef(out indexInKey);
		}

		private XElement PartRef(out int indexInKey)
		{
			indexInKey = -1;
			Debug.Assert(Key != null);
			if (Key == null)
			{
				return null;
			}
			for (var i = 0; i < Key.Length; i++)
			{
				var node = Key[i] as XElement;
				if (node == null || node.Name != "part" || XmlUtils.GetOptionalAttributeValue(node, "ref", null) == null)
				{
					continue;
				}
				indexInKey = i;
			}
			if (indexInKey != -1)
			{
				return (XElement)Key[indexInKey];
			}
			return null;
		}

		protected bool IsVisibilityItemChecked(string visibility)
		{
			CheckDisposed();

			XElement lastPartRef = null;
			foreach (var obj in Key)
			{
				var node = obj as XElement;
				if (node == null || node.Name != "part" || XmlUtils.GetOptionalAttributeValue(node, "ref", null) == null)
				{
					continue;
				}
				lastPartRef = node;
			}
			return lastPartRef != null && XmlUtils.GetOptionalAttributeValue(lastPartRef, "visibility", always) == visibility;
		}

		/// <summary>
		/// This is used to control the width of the slice when the data tree is being laid out.
		/// Any earlier width set is meaningless.
		/// Some slices can avoid doing a lot of work by ignoring earlier OnSizeChanged messages.
		/// </summary>
		protected internal virtual void SetWidthForDataTreeLayout(int width)
		{
			CheckDisposed();

			if (Width != width)
			{
				Width = width;
			}

			m_widthHasBeenSetByDataTree = true;
			m_splitContainer.Size = Size;
			m_splitContainer.SplitterMoved -= mySplitterMoved;
			if (!m_splitContainer.IsSplitterFixed)
			{
				m_splitContainer.SplitterMoved += mySplitterMoved;
			}
		}

		/// <summary>
		/// Note: There are two SplitterDistance event handlers on a Slice.
		/// This one handles the side effects of redrawing the tree node, when needed.
		/// Another one on DataTree takes care of updating the SplitterDisance on all the other slices.
		/// </summary>
		private void mySplitterMoved(object sender, SplitterEventArgs e)
		{
			if (!m_splitContainer.Panel1Collapsed)
			{
				TreeNode.Invalidate();
			}
		}

		/// <summary>
		/// Most slices are small, this keeps initial estimates more reasonable.
		/// </summary>
		protected override Size DefaultSize
		{
			get
			{
				CheckDisposed();

				return new Size(400, 20);
			}
		}

		/// <summary>
		/// This is called when clearing the slice collection, or otherwise about to remove a slice
		/// from its parent and discard it. It allows us to put views into a state where
		/// they won't waste time if they get an OnLoad message somewhere in the course of
		/// clearing them from the collection. (LT-3118 is one problem this helped with.)
		/// </summary>
		public virtual void AboutToDiscard()
		{
			CheckDisposed();
			BeingDiscarded = true;	// Remember that we're going away in case we need to know this for subsequent method calls.
		}

		/// <summary>
		/// Flag whether this slice is in the process of being thrown away.
		/// </summary>
		private bool BeingDiscarded { get; set; }

#region Implementation of IPropertyTableProvider

		/// <summary>
		/// Placement in the IPropertyTableProvider interface lets FwApp call IPropertyTable.DoStuff.
		/// </summary>
		public IPropertyTable PropertyTable { get; private set; }

#endregion

#region Implementation of IPublisherProvider

		/// <summary>
		/// Get the IPublisher.
		/// </summary>
		public IPublisher Publisher { get; private set; }

#endregion

#region Implementation of ISubscriberProvider

		/// <summary>
		/// Get the ISubscriber.
		/// </summary>
		public ISubscriber Subscriber { get; private set; }

		#endregion

		#region Implementation of IFlexComponent

		/// <summary>
		/// Initialize a FLEx component with the basic interfaces.
		/// </summary>
		/// <param name="flexComponentParameters">Parameter object that contains the required three interfaces.</param>
		public virtual void InitializeFlexComponent(FlexComponentParameters flexComponentParameters)
		{
			FlexComponentCheckingService.CheckInitializationValues(flexComponentParameters, new FlexComponentParameters(PropertyTable, Publisher, Subscriber));

			PropertyTable = flexComponentParameters.PropertyTable;
			Publisher = flexComponentParameters.Publisher;
			Subscriber = flexComponentParameters.Subscriber;

			if (Control is IFlexComponent)
			{
				((IFlexComponent)Control).InitializeFlexComponent(flexComponentParameters);
			}
			else if (Control != null)
			{
				// If not a SimpleRootSite, maybe it owns one. Init that as Flex component.
				for (var i = 0; i < Control.Controls.Count; ++i)
				{
					var fc = Control.Controls[i] as IFlexComponent;
					fc?.InitializeFlexComponent(flexComponentParameters);
				}
			}
		}

#endregion
	}
}