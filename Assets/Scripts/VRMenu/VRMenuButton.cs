// =============================================================================
//  VRMenuButton.cs
//
//  Hover and press feedback for one menu button.
//
//  Rides on the standard EventSystem callbacks, which XRUIInputModule raises for
//  a controller ray exactly as a mouse would - so no custom raycasting, and the
//  same button also works if you ever point at it with a poke interactor.
//
//  Effects are kept small on purpose: a button that leaps toward you in VR reads
//  as broken rather than responsive.
// =============================================================================

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace VRCinema
{
    [RequireComponent(typeof(Image))]
    public class VRMenuButton : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        [SerializeField] private Image background;
        [SerializeField] private TextMeshProUGUI label;

        [Header("Colours")]
        [SerializeField] private Color normalColor  = new Color(0.12f, 0.14f, 0.19f, 1f);
        [SerializeField] private Color hoverColor   = new Color(0.18f, 0.34f, 0.55f, 1f);
        [SerializeField] private Color pressedColor = new Color(0.10f, 0.45f, 0.80f, 1f);

        [SerializeField] private Color normalTextColor = new Color(0.86f, 0.89f, 0.94f, 1f);
        [SerializeField] private Color hoverTextColor  = Color.white;

        [Header("Motion")]
        [SerializeField, Range(1f, 1.15f)] private float hoverScale = 1.04f;
        [SerializeField, Range(0.85f, 1f)] private float pressedScale = 0.97f;
        [SerializeField, Range(1f, 40f)] private float sharpness = 18f;

        private bool _hovered, _pressed;
        private Vector3 _baseScale;

        private void Awake()
        {
            if (background == null) background = GetComponent<Image>();
            if (label == null) label = GetComponentInChildren<TextMeshProUGUI>();
            _baseScale = transform.localScale;
            Apply(instant: true);
        }

        private void OnDisable()
        {
            // The pointer never gets to exit a button that is switched off mid-hover.
            _hovered = _pressed = false;
            Apply(instant: true);
        }

        public void OnPointerEnter(PointerEventData e) { _hovered = true; }
        public void OnPointerExit(PointerEventData e)  { _hovered = false; _pressed = false; }
        public void OnPointerDown(PointerEventData e)  { _pressed = true; }
        public void OnPointerUp(PointerEventData e)    { _pressed = false; }

        private void Update() => Apply(instant: false);

        private void Apply(bool instant)
        {
            Color target = _pressed ? pressedColor : _hovered ? hoverColor : normalColor;
            float scale  = _pressed ? pressedScale : _hovered ? hoverScale : 1f;

            float t = instant ? 1f : 1f - Mathf.Exp(-sharpness * Time.unscaledDeltaTime);

            if (background != null)
                background.color = Color.Lerp(background.color, target, t);
            if (label != null)
                label.color = Color.Lerp(label.color, _hovered ? hoverTextColor : normalTextColor, t);

            transform.localScale = Vector3.Lerp(transform.localScale, _baseScale * scale, t);
        }
    }
}
