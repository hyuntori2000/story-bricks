using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// Hover and press feedback for a watercolour button: the wash deepens, the pencil line darkens
/// and the button grows slightly on hover, then dips on press.
public class WashButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public Image wash, pencil;
    public Color hoverTint = new Color(0.94f, 0.87f, 0.82f);
    public Color pressTint = new Color(0.86f, 0.77f, 0.71f);
    public float hoverScale = 1.05f, pressScale = 0.98f;

    bool hover, down, ready;
    float flash, h, p;
    Color washBase = Color.white, pencilBase = Color.white;

    void Awake()
    {
        if (wash != null) washBase = wash.color;
        if (pencil != null) pencilBase = pencil.color;
        ready = true;
    }

    void OnDisable() { hover = down = false; flash = 0f; h = p = 0f; if (ready) Apply(); }

    /// Shows the pressed look briefly, for choices made by voice or controller button.
    public void Flash() { flash = 0.3f; }

    public void OnPointerEnter(PointerEventData e) { hover = true; }
    public void OnPointerExit(PointerEventData e) { hover = false; down = false; }
    public void OnPointerDown(PointerEventData e) { down = true; }
    public void OnPointerUp(PointerEventData e) { down = false; }

    void Update()
    {
        flash -= Time.unscaledDeltaTime;
        float dt = Time.unscaledDeltaTime * 10f;
        bool pressed = down || flash > 0f;
        h = Mathf.MoveTowards(h, hover || pressed ? 1f : 0f, dt);
        p = Mathf.MoveTowards(p, pressed ? 1f : 0f, dt);
        Apply();
    }

    void Apply()
    {
        if (wash != null)
        {
            Color c = Color.Lerp(Color.Lerp(Color.white, hoverTint, h), pressTint, p);
            wash.color = new Color(washBase.r * c.r, washBase.g * c.g, washBase.b * c.b, washBase.a);
        }
        if (pencil != null)
            pencil.color = Color.Lerp(pencilBase, new Color(pencilBase.r * 0.62f, pencilBase.g * 0.62f, pencilBase.b * 0.62f, 1f), h);
        transform.localScale = Vector3.one * Mathf.Lerp(Mathf.Lerp(1f, hoverScale, h), pressScale, p);
    }
}
