// =============================================================================
//  GeckoUIButton.cs
//
//  A clickable quad in world space. Deliberately NOT a uGUI Button:
//
//   - uGUI needs a Canvas, an EventSystem and an input module that understands
//     an XR ray. This project has none of those, and adding them would mean
//     pulling in the XR Interaction Toolkit rig.
//   - Hitting a BoxCollider and calling onClick is the whole mechanism.
//
//  Who drives it: GeckoXRButton bridges XRI hover/select onto SetHovered /
//  SetPressed / Activate. In scenes still on the old raycasting path,
//  CinemaPointerInput calls the same three methods directly.
//
//  Created at runtime by GeckoBrowserUI - you never add this in the Inspector.
// =============================================================================

using UnityEngine;

[RequireComponent(typeof(BoxCollider))]
public class GeckoUIButton : MonoBehaviour
{
    /// <summary>Invoked on trigger release while the ray is on this button.</summary>
    public System.Action onClick;

    /// <summary>Free-form data - GeckoBrowserUI uses it to carry keyboard chars.</summary>
    public string payload;

    private Renderer _renderer;
    private MaterialPropertyBlock _block;

    // Hover has to survive a dark room. The old hover was 0.26,0.29,0.34 against
    // a 0.16,0.17,0.20 normal - a tenth of a step apart before the scene's dim
    // lighting multiplied both toward black, which is why picking a key was
    // guesswork. Hover is now a saturated cyan several times brighter than the
    // keycap, and pressed is brighter still.
    private Color _normal   = new Color(0.16f, 0.17f, 0.20f, 1f);
    private Color _hover    = new Color(0.15f, 0.62f, 0.95f, 1f);
    private Color _pressed  = new Color(0.35f, 0.85f, 1.00f, 1f);
    private Color _disabled = new Color(0.12f, 0.12f, 0.13f, 1f);

    private bool _interactable = true;
    private bool _isHovered;
    private bool _isPressed;

    public bool Interactable
    {
        get => _interactable;
        set { _interactable = value; Refresh(); }
    }

    /// <summary>
    /// Raised as each button comes to life. GeckoBrowserUI and GeckoPageKeyboard
    /// build their buttons at runtime, long after the scene loads, so there is no
    /// other moment at which something can attach behaviour to all of them.
    /// GeckoXRInteraction uses this to give every button an XR interactable.
    /// </summary>
    public static System.Action<GeckoUIButton> Created;

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _block = new MaterialPropertyBlock();
        Refresh();
        Created?.Invoke(this);
    }

    public void SetColors(Color normal, Color hover, Color pressed)
    {
        _normal = normal; _hover = hover; _pressed = pressed;
        Refresh();
    }

    public void SetHovered(bool hovered)
    {
        if (_isHovered == hovered) return;
        _isHovered = hovered;
        Refresh();
    }

    public void SetPressed(bool pressed)
    {
        if (_isPressed == pressed) return;
        _isPressed = pressed;
        Refresh();
    }

    /// <summary>Called on release while the ray is still on this button.</summary>
    public void Activate()
    {
        if (!_interactable) return;
        onClick?.Invoke();
    }

    private void Refresh()
    {
        if (_renderer == null) return;

        Color c = !_interactable ? _disabled
                : _isPressed     ? _pressed
                : _isHovered     ? _hover
                                 : _normal;

        // A property block avoids instantiating a material per button, which
        // would be dozens of draw-call-breaking material instances for the
        // on-screen keyboard alone.
        _renderer.GetPropertyBlock(_block);
        _block.SetColor("_BaseColor", c);   // URP
        _block.SetColor("_Color", c);       // Built-in fallback
        // Lit shaders multiply the base colour by whatever light reaches the
        // quad, and a cinema has almost none. Emission is added after lighting,
        // so the highlight reads the same in a blacked-out auditorium as it does
        // in the editor. Harmless on Unlit, which ignores it.
        _block.SetColor("_EmissionColor", (_isHovered || _isPressed) ? c * 0.9f : Color.black);
        _renderer.SetPropertyBlock(_block);
    }
}
