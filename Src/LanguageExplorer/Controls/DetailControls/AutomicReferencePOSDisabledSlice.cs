// Copyright (c) 2012-2018 SIL International
// This software is licensed under the LGPL, version 2.1 or later
// (http://www.gnu.org/licenses/lgpl-2.1.html)

using System.Drawing;
using SIL.FieldWorks.Common.FwUtils;
using SIL.LCModel;

namespace LanguageExplorer.Controls.DetailControls
{
	/// <summary>
	/// This class shows the POS slice as being disabled.
	/// </summary>
	internal class AutomicReferencePOSDisabledSlice : AtomicReferencePOSSlice
	{
		/// <summary />
		internal AutomicReferencePOSDisabledSlice(LcmCache cache, ICmObject obj, int flid, IPropertyTable propertyTable, IPublisher publisher)
			: base(cache, obj, flid, propertyTable, publisher)
		{
			if (Tree != null)
			{
				Tree.ForeColor = SystemColors.GrayText;
			}
		}
	}
}