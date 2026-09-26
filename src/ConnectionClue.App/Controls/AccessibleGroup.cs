using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ConnectionClue.App.Controls;

/// <summary>
/// A focusable container that UI Automation sees as one named item. Panels have no automation peer, and text inside
/// data templates is hidden from the control view, so without this screen readers would skip path steps, chart rows
/// and recommendations. The name comes from AutomationProperties.Name. Focusable and IsTabStop keep the Control
/// defaults (true) rather than local values, so a template can turn them off (headings).
/// </summary>
public sealed class AccessibleGroup : ContentControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(AccessibleGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetClassNameCore() => nameof(AccessibleGroup);

        // The name carries the text; only interactive descendants (links, buttons) are exposed, so they stay reachable.
        protected override List<AutomationPeer>? GetChildrenCore()
        {
            var interactive = new List<AutomationPeer>();
            Collect(base.GetChildrenCore(), interactive);
            return interactive.Count == 0 ? null : interactive;
        }

        private static void Collect(List<AutomationPeer>? peers, List<AutomationPeer> into)
        {
            foreach (var peer in peers ?? [])
            {
                if (peer.GetAutomationControlType() is AutomationControlType.Hyperlink or AutomationControlType.Button) into.Add(peer);
                else Collect(peer.GetChildren(), into);
            }
        }
    }
}
