// =============================================================================
//  GeckoVulkanRenderer.cs
//
//  Drives the GeckoView -> AHardwareBuffer -> VkImage bridge and paints the
//  result onto this GameObject's Renderer.
//
//  Attach to the 3D Plane. Requires:
//    - Graphics API   : Vulkan (only)
//    - Minimum API    : 29
//    - Platform       : Android / Meta Quest 3
//
//  Log tag on the native side: GeckoVulkanBridge
// =============================================================================

using System;
using System.Collections;
using System.Runtime.InteropServices;
using UnityEngine;

[RequireComponent(typeof(Renderer))]
public class GeckoVulkanRenderer : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Inspector
    // -------------------------------------------------------------------------
    [Header("Browser")]
    [Tooltip("Page loaded once the pipeline is up.")]
    public string url = "https://example.com";

    [Tooltip("Texture width in pixels. Must match the plane's aspect ratio or the " +
             "page will look stretched. 1920x1080 is a good Quest 3 default.")]
    public int textureWidth = 1920;
    public int textureHeight = 1080;

    [Header("Presentation")]
    [Tooltip("GL renders bottom-up, Unity samples top-down. Leave on unless the " +
             "page appears upside down.")]
    public bool flipVertically = true;

    [Tooltip("Mirror the page left-to-right. Needed on this setup: the combination " +
             "of the SurfaceTexture transform and the plane's -90 X rotation lands " +
             "the page mirrored. Turn off if text reads backwards.")]
    public bool flipHorizontally = true;

    [Tooltip("Set true only if your project is in Linear colour space AND you have " +
             "verified the page looks washed out without it. See notes below.")]
    public bool treatAsLinear = false;

    [Header("Sharpness")]
    [Tooltip("Page device-pixel-ratio. THE main lever on text clarity. At 1.0 a " +
             "3840px surface is a 3840 CSS-px desktop window - huge page, tiny " +
             "text, blurry when magnified. At 2.0 it's a 1920 CSS-px window " +
             "rendered at 2x: same apparent size, 4x the glyph detail. " +
             "Must be set before the Gecko runtime starts.")]
    [Range(1f, 4f)] public float pageDensity = 2f;

    [Tooltip("Anisotropic filtering. The plane is usually viewed at an angle, " +
             "where bilinear alone smears texels along the slanted axis.")]
    [Range(0, 16)] public int anisoLevel = 8;

    [Tooltip("Unity's XR eye-texture scale. Quest often renders below panel " +
             "resolution by default, and small text is the first casualty. " +
             "1.2-1.4 sharpens everything; 0 leaves Unity's default alone. " +
             "Costs GPU across the whole scene, not just the browser.")]
    public float xrRenderScale = 0f;

    [Header("Lifecycle")]
    [Tooltip("Start the native WebView/Vulkan pipeline as soon as this object " +
             "wakes up. Turn off for a BrowserPlane instance that a cinema " +
             "system (or anything else) activates on demand - call " +
             "BeginInitialise() when it should actually go live, and " +
             "ShutdownBridge() to tear it down again without destroying this " +
             "GameObject. Standalone usage is unaffected: this defaults to on.")]
    public bool autoInitialize = true;

    [Header("Debug")]
    public bool verboseLogging = true;

    // -------------------------------------------------------------------------
    // Native interop
    //
    // On Android the library name drops the "lib" prefix and the ".so" suffix.
    // -------------------------------------------------------------------------
    private const string NativeLib = "geckovulkanbridge";

    /// <summary>
    /// Imports the AHardwareBuffer as a VkImage on Unity's VkDevice and returns
    /// a pointer to the VkImage handle (Unity's Vulkan backend expects VkImage*,
    /// not the raw 64-bit handle). Returns IntPtr.Zero on failure.
    /// </summary>
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetVulkanImagePointer(IntPtr unityVulkanDevice);

    /// <summary>Render-thread callback that performs the per-frame image barrier.</summary>
    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetRenderEventFunc();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void UnityShutdownVulkanImage();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetBridgeWidth();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetBridgeHeight();

    [DllImport(NativeLib, CallingConvention = CallingConvention.Cdecl)]
    private static extern long GetFramesProduced();

    private const int kAcquireImageEventId = 1;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private AndroidJavaObject _plugin;
    private Texture2D _externalTexture;
    private Renderer _renderer;
    private Material _material;
    private IntPtr _renderEventFunc = IntPtr.Zero;
    private bool _pipelineLive;
    private string _homeUrl;

    private const string Tag = "[GeckoVulkanBridge] ";

    private void Log(string msg)
    {
        if (verboseLogging) Debug.Log(Tag + msg);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------
    private void Awake()
    {
        // The authored URL is the home page. Captured before anything can
        // navigate, because LoadUrl() overwrites `url` as it goes.
        _homeUrl = url;
        _renderer = GetComponent<Renderer>();
        // Instance the material so we don't mutate the shared asset.
        _material = _renderer.material;
    }

    private void Start()
    {
        // Guard: everything below is Vulkan-on-Android only.
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
        {
            Debug.LogError(Tag + "Graphics API is " + SystemInfo.graphicsDeviceType +
                           ", not Vulkan. Remove every other API from " +
                           "Player Settings > Other Settings > Graphics APIs.");
            enabled = false;
            return;
        }
#if !UNITY_ANDROID || UNITY_EDITOR
        Debug.LogWarning(Tag + "Not running on an Android device - bridge disabled.");
        enabled = false;
        return;
#else
        Log("Vulkan confirmed. Device: " + SystemInfo.graphicsDeviceName +
            " / " + SystemInfo.graphicsDeviceVersion);

        if (xrRenderScale > 0f)
        {
            float before = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
            UnityEngine.XR.XRSettings.eyeTextureResolutionScale = xrRenderScale;
            Log($"XR eye texture scale {before:F2} -> {xrRenderScale:F2}");
        }
        Log($"page density={pageDensity} surface={textureWidth}x{textureHeight} " +
            $"aniso={anisoLevel}");
        _platformOk = true;
        if (autoInitialize) BeginInitialise();
#endif
    }

    private bool _platformOk;
    private bool _initialising;

    /// <summary>
    /// Starts the native WebView/Vulkan pipeline. Safe to call more than once -
    /// a no-op while already live or already starting. This is the entry point
    /// for anything that sets <see cref="autoInitialize"/> to false and wants to
    /// bring the bridge up on its own schedule (a cinema system activating the
    /// screen for the theater the player just entered, for example).
    /// </summary>
    public void BeginInitialise()
    {
        if (!_platformOk)
        {
            Debug.LogWarning(Tag + "BeginInitialise() called but the platform guard " +
                             "in Start() never passed (wrong graphics API, or not an " +
                             "Android device) - nothing to start.");
            return;
        }
        if (_pipelineLive || _initialising) return;
        _initialising = true;
        StartCoroutine(InitialiseBridge());
    }

    private IEnumerator InitialiseBridge()
    {
      try
      {
        // --- 1. Instantiate the Kotlin plugin --------------------------------
        AndroidJavaObject activity;
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        }
        if (activity == null)
        {
            Debug.LogError(Tag + "UnityPlayer.currentActivity is null.");
            yield break;
        }

        try
        {
            _plugin = new AndroidJavaObject("com.example.geckovulkan.GeckoVulkanPlugin", activity);
            Log("GeckoVulkanPlugin instantiated.");

            // Before initialize(): GeckoRuntime is built on the first initialize()
            // call and displayDensityOverride is a Builder-only option.
            _plugin.Call("setDisplayDensity", pageDensity);

            _plugin.Call("initialize", textureWidth, textureHeight, url);
            Log($"initialize({textureWidth}, {textureHeight}, \"{url}\") issued.");
        }
        catch (Exception e)
        {
            Debug.LogError(Tag + "Failed to start the Kotlin plugin: " + e);
            yield break;
        }

        // --- 2. Wait for the AHardwareBuffer to exist ------------------------
        // initialize() is asynchronous: EGL setup and buffer allocation happen on
        // the plugin's private GL thread. Poll isReady rather than sleeping a
        // fixed amount.
        const float timeoutSeconds = 15f;
        float waited = 0f;
        while (!_plugin.Call<bool>("isReady"))
        {
            waited += Time.deltaTime;
            if (waited > timeoutSeconds)
            {
                string err = _plugin.Call<string>("getLastError");
                Debug.LogError(Tag + "Timed out waiting for the native buffer. " +
                               "lastError=" + (err ?? "<none>") +
                               "  -- check logcat -s GeckoVulkanBridge");
                yield break;
            }
            yield return null;
        }
        Log($"Native side ready after {waited:F2}s.");

        // --- 3. Import the buffer into Unity's Vulkan device -----------------
        // The device argument is accepted for API symmetry; native resolves the
        // authoritative VkDevice through IUnityGraphicsVulkan::Instance(), which
        // is the only device the image will be valid on.
        IntPtr vkImagePtr = GetVulkanImagePointer(IntPtr.Zero);
        if (vkImagePtr == IntPtr.Zero)
        {
            Debug.LogError(Tag + "GetVulkanImagePointer returned null. The most likely " +
                           "cause is that vkCreateDevice interception did not run: verify " +
                           "GeckoUnityPlayerActivity is the launcher activity so the .so " +
                           "loads before Unity's graphics device is created.");
            yield break;
        }
        Log("VkImage* = 0x" + vkImagePtr.ToString("X"));

        int w = GetBridgeWidth();
        int h = GetBridgeHeight();
        Log($"Native buffer is {w}x{h}.");

        // --- 4. Wrap it in a Texture2D ---------------------------------------
        // TextureFormat.RGBA32 matches VK_FORMAT_R8G8B8A8_UNORM. mipChain must be
        // false: the imported image has exactly one mip level.
        _externalTexture = Texture2D.CreateExternalTexture(
            w, h,
            TextureFormat.RGBA32,
            /* mipChain */ false,
            /* linear   */ treatAsLinear,
            vkImagePtr);

        if (_externalTexture == null)
        {
            Debug.LogError(Tag + "CreateExternalTexture returned null.");
            yield break;
        }
        _externalTexture.name = "GeckoExternalTexture";
        _externalTexture.wrapMode   = TextureWrapMode.Clamp;
        _externalTexture.filterMode = FilterMode.Bilinear;
        _externalTexture.anisoLevel = anisoLevel;

        // --- 5. Bind to the plane --------------------------------------------
        _material.mainTexture = _externalTexture;
        ApplyTextureTransform();
        Log("Texture bound to " + _renderer.name + " (" + _material.shader.name + ").");

        // --- 6. Start pumping the per-frame barrier --------------------------
        _renderEventFunc = GetRenderEventFunc();
        if (_renderEventFunc == IntPtr.Zero)
        {
            Debug.LogError(Tag + "GetRenderEventFunc returned null - the image will " +
                           "never leave VK_IMAGE_LAYOUT_UNDEFINED.");
            yield break;
        }
        _pipelineLive = true;
        StartCoroutine(IssueAcquireBarrierEachFrame());

        if (verboseLogging) StartCoroutine(LogThroughput());
      }
      finally { _initialising = false; }
    }

    /// <summary>
    /// Once per frame, on Unity's render thread: transition the imported image
    /// out of the foreign (GLES) queue and into SHADER_READ_ONLY_OPTIMAL.
    /// WaitForEndOfFrame keeps this after Unity's own rendering work is queued.
    /// </summary>
    private IEnumerator IssueAcquireBarrierEachFrame()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (_pipelineLive)
        {
            yield return endOfFrame;
            GL.IssuePluginEvent(_renderEventFunc, kAcquireImageEventId);
        }
    }

    private IEnumerator LogThroughput()
    {
        var wait = new WaitForSeconds(5f);
        long last = 0;
        while (_pipelineLive)
        {
            yield return wait;
            long now = GetFramesProduced();
            Log($"Gecko frames blitted: {now} (+{now - last} in 5s)");
            if (now == last && now == 0)
            {
                Debug.LogWarning(Tag + "No frames produced yet. Either the page hasn't " +
                                 "painted or GeckoDisplay never got the surface.");
            }
            last = now;
        }
    }

    /// <summary>
    /// Applies the flip toggles to the material's UV transform.
    /// scale -1 / offset +1 on an axis mirrors it: sampled = 1 - original.
    /// GeckoPointerInput reads the same two flags so clicks stay aligned with
    /// what you actually see - change these and the input follows automatically.
    /// </summary>
    private void ApplyTextureTransform()
    {
        Vector2 scale  = new Vector2(flipHorizontally ? -1f : 1f,
                                     flipVertically   ? -1f : 1f);
        Vector2 offset = new Vector2(flipHorizontally ?  1f : 0f,
                                     flipVertically   ?  1f : 0f);
        _material.mainTextureScale  = scale;
        _material.mainTextureOffset = offset;
        Log($"UV transform: scale={scale} offset={offset} " +
            $"(flipH={flipHorizontally}, flipV={flipVertically})");
    }

    // -------------------------------------------------------------------------
    // Public control surface
    // -------------------------------------------------------------------------
    public void LoadUrl(string newUrl)
    {
        if (_plugin == null) { Debug.LogWarning(Tag + "LoadUrl before init."); return; }
        url = newUrl;
        _plugin.Call("loadUrl", newUrl);
    }

    public void Reload()    => _plugin?.Call("reload");

    /// <summary>
    /// Back to the URL this component was authored with. Parameterless so a
    /// UnityEvent - the floating menu's HOME PAGE button - can call it without
    /// a second copy of the address to keep in sync.
    /// </summary>
    public void GoHome() => LoadUrl(_homeUrl);

    /// <summary>Current page URL, mirrored from Gecko's NavigationDelegate.</summary>
    public string GetCurrentUrl()
    {
        if (_plugin == null) return "";
        try { return _plugin.Call<string>("getCurrentUrl") ?? ""; }
        catch { return ""; }
    }

    public bool CanGoBack()
    {
        if (_plugin == null) return false;
        try { return _plugin.Call<bool>("getCanGoBack"); } catch { return false; }
    }

    public bool CanGoForward()
    {
        if (_plugin == null) return false;
        try { return _plugin.Call<bool>("getCanGoForward"); } catch { return false; }
    }
    public void GoBack()    => _plugin?.Call("goBack");
    public void GoForward() => _plugin?.Call("goForward");

    // -------------------------------------------------------------------------
    // Input injection (driven by GeckoPointerInput)
    //
    // Coordinates are surface pixels, y = 0 at the TOP of the page.
    // -------------------------------------------------------------------------

    /// <summary>True once the page is live and able to accept input.</summary>
    public bool IsInteractive => _plugin != null && _pipelineLive;

    /// <summary>Texture size in pixels, for UV -> pixel conversion.</summary>
    public int SurfaceWidth  => textureWidth;
    public int SurfaceHeight => textureHeight;

    /// <param name="action">0 = down, 1 = up, 2 = move, 3 = cancel</param>
    public void SendTouch(int action, float x, float y)
    {
        if (_plugin == null) return;
        _plugin.Call("injectTouch", action, x, y);
    }

    /// <summary>Moves the synthetic mouse cursor so the page fires CSS :hover.</summary>
    public void SendHover(float x, float y)
    {
        if (_plugin == null) return;
        _plugin.Call("injectHover", x, y);
    }

    /// <summary>Cursor left the page - clears Gecko's current :hover state.</summary>
    public void SendHoverExit(float x, float y)
    {
        if (_plugin == null) return;
        _plugin.Call("injectHoverExit", x, y);
    }

    /// <summary>Wheel scroll at (x,y). Positive deltaY scrolls the page down.</summary>
    public void SendScroll(float x, float y, float deltaX, float deltaY)
    {
        if (_plugin == null) return;
        _plugin.Call("injectScroll", x, y, deltaX, deltaY);
    }

    // -------------------------------------------------------------------------
    // Text entry (driven by GeckoPageKeyboard)
    //
    // The native plugin exposes touch, hover and scroll only - there is no key
    // or IME entry point on the AAR, so typing goes in as script instead. The
    // WebView underneath has JavaScript enabled and loadUrl() passes the string
    // through unfiltered, so a "javascript:" URI evaluates in the page without
    // navigating away from it.
    //
    // The whole body is percent-encoded: an un-encoded '#' would be taken as a
    // fragment and silently truncate the script, and spaces and quotes fare no
    // better inside a URI.
    // -------------------------------------------------------------------------

    /// <summary>Evaluates <paramref name="js"/> in the page. No return value -
    /// the plugin has no callback channel back into Unity.</summary>
    public void SendJavaScript(string js)
    {
        if (_plugin == null || string.IsNullOrEmpty(js)) return;
        _plugin.Call("loadUrl", "javascript:" + Uri.EscapeDataString(js));
        if (verboseLogging) Log("js: " + (js.Length > 120 ? js.Substring(0, 120) + "..." : js));
    }

    /// <summary>
    /// Inserts <paramref name="text"/> at the caret of the focused element.
    /// Carried as base64 so quotes, backslashes and non-ASCII in the payload can
    /// never break out of the script literal.
    /// </summary>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        SendJavaScript(JsPrelude + "var t=D('" + b64 + "');" + JsInsert + "})()");
    }

    /// <summary>Deletes the selection, or one character before the caret.</summary>
    public void SendBackspace() => SendJavaScript(JsPrelude + JsBackspace + "})()");

    /// <summary>Enter: submits the owning form if there is one, and fires the
    /// key events either way so script-driven search boxes react.</summary>
    public void SendEnter() => SendJavaScript(JsPrelude + JsEnter + "})()");

    /// <summary>Clears the focused field outright.</summary>
    public void SendClearField() => SendJavaScript(JsPrelude + JsClear + "})()");

    /// <summary>Drops focus, so the page's own soft-keyboard affordances close.</summary>
    public void SendBlur() =>
        SendJavaScript("(function(){var e=document.activeElement;if(e&&e.blur)e.blur();})()");

    // Shared head: resolves the focused element, walking into an iframe when the
    // focus sits inside one, and defines the base64 decoder and the value setter.
    //
    // The setter goes through the prototype descriptor rather than e.value = x
    // because frameworks that wrap the input (React and friends) patch the
    // instance property and would otherwise never see the change.
    private const string JsPrelude =
        "(function(){" +
        "var d=document;" +
        "try{while(d.activeElement&&d.activeElement.contentDocument)" +
        "d=d.activeElement.contentDocument;}catch(x){}" +
        "var e=d.activeElement;if(!e)return;" +
        "var D=function(b){return decodeURIComponent(escape(atob(b)));};" +
        "var S=function(el,v){" +
        "var p=el instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;" +
        "var s=Object.getOwnPropertyDescriptor(p,'value');" +
        "if(s&&s.set)s.set.call(el,v);else el.value=v;" +
        "el.dispatchEvent(new Event('input',{bubbles:true}));" +
        "el.dispatchEvent(new Event('change',{bubbles:true}));};";

    private const string JsInsert =
        "if(e.isContentEditable){d.execCommand('insertText',false,t);return;}" +
        "if(!('value' in e))return;" +
        "var a=e.selectionStart,b=e.selectionEnd;" +
        "if(a==null){S(e,(e.value||'')+t);return;}" +
        "S(e,e.value.slice(0,a)+t+e.value.slice(b));" +
        "try{e.selectionStart=e.selectionEnd=a+t.length;}catch(x){}";

    private const string JsBackspace =
        "if(e.isContentEditable){d.execCommand('delete');return;}" +
        "if(!('value' in e))return;" +
        "var a=e.selectionStart,b=e.selectionEnd;" +
        "if(a==null){S(e,(e.value||'').slice(0,-1));return;}" +
        "if(a==b){if(a==0)return;a=a-1;}" +
        "S(e,e.value.slice(0,a)+e.value.slice(b));" +
        "try{e.selectionStart=e.selectionEnd=a;}catch(x){}";

    private const string JsClear =
        "if(e.isContentEditable){e.textContent='';return;}" +
        "if('value' in e)S(e,'');";

    private const string JsEnter =
        "var k=function(y){return new KeyboardEvent(y,{key:'Enter',code:'Enter'," +
        "keyCode:13,which:13,bubbles:true,cancelable:true});};" +
        "var go=e.dispatchEvent(k('keydown'));" +
        "e.dispatchEvent(k('keypress'));e.dispatchEvent(k('keyup'));" +
        "if(go&&e.form){try{if(e.form.requestSubmit)e.form.requestSubmit();" +
        "else e.form.submit();}catch(x){}}";

    // -------------------------------------------------------------------------
    // Teardown. Order matters: drop the Unity texture first, then destroy the
    // VkImage/AHardwareBuffer, then stop Gecko.
    // -------------------------------------------------------------------------
    private void OnDestroy() => TeardownBridge("OnDestroy");

    /// <summary>
    /// Tears down the native WebView/Vulkan pipeline WITHOUT destroying this
    /// GameObject - the counterpart to <see cref="BeginInitialise"/>. Call this
    /// to free the WebView/AHardwareBuffer while the screen isn't the one being
    /// watched (a cinema system deactivating a theater the player just left),
    /// then call BeginInitialise() again later to bring it back on the same
    /// instance. Safe to call when nothing is running.
    /// </summary>
    public void ShutdownBridge() => TeardownBridge("ShutdownBridge");

    // Order matters: drop the Unity texture first, then destroy the
    // VkImage/AHardwareBuffer, then stop Gecko.
    private void TeardownBridge(string reason)
    {
        if (!_pipelineLive && _plugin == null) return;   // already down

        Log(reason + " - tearing down.");
        _pipelineLive = false;

        if (_material != null) _material.mainTexture = null;

        if (_externalTexture != null)
        {
            // Destroy(), not DestroyImmediate(): the external texture only wraps
            // the handle, it does not own the VkImage.
            Destroy(_externalTexture);
            _externalTexture = null;
        }

        try { UnityShutdownVulkanImage(); }
        catch (Exception e) { Debug.LogError(Tag + "UnityShutdownVulkanImage failed: " + e); }

        if (_plugin != null)
        {
            try { _plugin.Call("shutdown"); }
            catch (Exception e) { Debug.LogError(Tag + "plugin.shutdown failed: " + e); }
            _plugin.Dispose();
            _plugin = null;
        }
    }

    private void OnApplicationPause(bool paused)
    {
        Log("OnApplicationPause(" + paused + ")");
        // Gecko keeps compositing while the headset is off the head; if that costs
        // too much battery, call _plugin.Call("shutdown") here and re-initialise
        // on resume.
    }
}
