using GPConf;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.OpenGL3;
using Hexa.NET.ImGui.Backends.SDL3;
using Hexa.NET.ImPlot;
using Hexa.NET.OpenGL;
using Hexa.NET.SDL3;
using SDLEvent = Hexa.NET.SDL3.SDLEvent;
using SDLWindow = Hexa.NET.SDL3.SDLWindow;

SDL.SetHint(SDL.SDL_HINT_MOUSE_FOCUS_CLICKTHROUGH, "1");
SDL.Init(SDLInitFlags.Events | SDLInitFlags.Video);

// GL attributes must be set before window creation
SDL.GLSetAttribute(SDLGLAttr.ContextMajorVersion, 3);
SDL.GLSetAttribute(SDLGLAttr.ContextMinorVersion, 3);
SDL.GLSetAttribute(SDLGLAttr.ContextProfileMask, 1); // SDL_GL_CONTEXT_PROFILE_CORE
SDL.GLSetAttribute(SDLGLAttr.Doublebuffer, 1);
SDL.GLSetAttribute(SDLGLAttr.DepthSize, 24);
SDL.GLSetAttribute(SDLGLAttr.StencilSize, 8);

unsafe
{
    var window = SDL.CreateWindow("GPConf", 1280, 720, SDLWindowFlags.Resizable | SDLWindowFlags.Opengl);
    var windowId = SDL.GetWindowID(window);

    var guiContext = ImGui.CreateContext();
    ImGui.SetCurrentContext(guiContext);

    var imPlotContext = ImPlot.CreateContext();
    ImPlot.SetCurrentContext(imPlotContext);
    ImPlot.SetImGuiContext(guiContext);

    var io = ImGui.GetIO();
    io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
    io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;

    var glContext = SDL.GLCreateContext(window);
    SDL.GLMakeCurrent(window, glContext);
    SDL.GLSetSwapInterval(1);

    ImGuiImplSDL3.SetCurrentContext(guiContext);
    ImGuiImplSDL3.InitForOpenGL(new SDLWindowPtr((Hexa.NET.ImGui.Backends.SDL3.SDLWindow*)window), (void*)glContext.Handle);

    ImGuiImplOpenGL3.SetCurrentContext(guiContext);
    ImGuiImplOpenGL3.Init((byte*)null);

    GL gl = new(new BindingsContext(window, glContext));

    GpConfApp app = new GpConfApp();

    SDLEvent sdlEvent = default;
    bool exiting = false;

    while (!exiting)
    {
        SDL.PumpEvents();

        while (SDL.PollEvent(ref sdlEvent))
        {
            ImGuiImplSDL3.ProcessEvent((Hexa.NET.ImGui.Backends.SDL3.SDLEvent*)&sdlEvent);

            switch ((SDLEventType)sdlEvent.Type)
            {
                case SDLEventType.Quit:
                    exiting = true;
                    break;

                case SDLEventType.WindowCloseRequested:
                    if (sdlEvent.Window.WindowID == windowId)
                        exiting = true;
                    break;
            }
        }

        gl.MakeCurrent();
        gl.ClearColor(0, 0, 0, 1);
        gl.Clear(GLClearBufferMask.ColorBufferBit);

        ImGuiImplOpenGL3.NewFrame();
        ImGuiImplSDL3.NewFrame();
        ImGui.NewFrame();

        // App UI here
        //ImGui.ShowDemoWindow();
        //ImPlot.ShowDemoWindow();

        app.Update();

        ImGui.Render();

        gl.MakeCurrent();
        ImGuiImplOpenGL3.RenderDrawData(ImGui.GetDrawData());

        gl.SwapBuffers();
    }

    ImPlot.DestroyContext(imPlotContext);
    ImGuiImplOpenGL3.Shutdown();
    ImGuiImplSDL3.Shutdown();
    ImGui.DestroyContext(guiContext);
    gl.Dispose();

    SDL.DestroyWindow(window);
    SDL.Quit();
}

unsafe class BindingsContext : HexaGen.Runtime.IGLContext
{
    private readonly SDLWindow* _window;
    private readonly SDLGLContext _context;

    public BindingsContext(SDLWindow* window, SDLGLContext context)
    {
        _window = window;
        _context = context;
    }

    public nint Handle => (nint)_window;
    public bool IsCurrent => SDL.GLGetCurrentContext() == _context;
    public void Dispose() { }
    public nint GetProcAddress(string procName) => (nint)SDL.GLGetProcAddress(procName);
    public bool IsExtensionSupported(string extensionName) => SDL.GLExtensionSupported(extensionName);
    public void MakeCurrent() => SDL.GLMakeCurrent(_window, _context);
    public void SwapBuffers() => SDL.GLSwapWindow(_window);
    public void SwapInterval(int interval) => SDL.GLSetSwapInterval(interval);

    public bool TryGetProcAddress(string procName, out nint procAddress)
    {
        procAddress = (nint)SDL.GLGetProcAddress(procName);
        return procAddress != 0;
    }
}
