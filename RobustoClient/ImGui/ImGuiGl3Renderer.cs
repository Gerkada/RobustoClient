using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using Robust.Client.Graphics;

namespace RobustoClient.ImGui;

public sealed unsafe class ImGuiGl3Renderer : IDisposable
{
    private const uint GL_VERSION = 0x1F02;
    private const uint GL_TEXTURE_BINDING_2D = 0x8069;
    private const uint GL_ARRAY_BUFFER_BINDING = 0x8894;
    private const uint GL_VERTEX_ARRAY_BINDING = 0x85B5;
    private const uint GL_ACTIVE_TEXTURE = 0x84E0;
    private const uint GL_TEXTURE0 = 0x84C0;
    private const uint GL_CURRENT_PROGRAM = 0x8B8D;
    private const uint GL_VIEWPORT = 0x0BA2;
    private const uint GL_SCISSOR_BOX = 0x0C10;
    private const uint GL_BLEND_SRC_RGB = 0x80C9;
    private const uint GL_BLEND_DST_RGB = 0x80C8;
    private const uint GL_BLEND_SRC_ALPHA = 0x80CA;
    private const uint GL_BLEND_DST_ALPHA = 0x80CB;
    private const uint GL_BLEND_EQUATION_RGB = 0x8009;
    private const uint GL_BLEND_EQUATION_ALPHA = 0x883D;
    private const uint GL_BLEND = 0x0BE2;
    private const uint GL_CULL_FACE = 0x0B44;
    private const uint GL_DEPTH_TEST = 0x0B71;
    private const uint GL_STENCIL_TEST = 0x0B90;
    private const uint GL_SCISSOR_TEST = 0x0C11;
    private const uint GL_FRAMEBUFFER_SRGB = 0x8DB9;
    private const uint GL_FUNC_ADD = 0x8006;
    private const uint GL_SRC_ALPHA = 0x0302;
    private const uint GL_ONE_MINUS_SRC_ALPHA = 0x0303;
    private const uint GL_ONE = 1;
    private const uint GL_ARRAY_BUFFER = 0x8892;
    private const uint GL_ELEMENT_ARRAY_BUFFER = 0x8893;
    private const uint GL_STREAM_DRAW = 0x88E0;
    private const uint GL_FLOAT = 0x1406;
    private const uint GL_UNSIGNED_BYTE = 0x1401;
    private const uint GL_UNSIGNED_SHORT = 0x1403;
    private const uint GL_TRIANGLES = 0x0004;
    private const uint GL_TEXTURE_2D = 0x0DE1;
    private const uint GL_TEXTURE_MIN_FILTER = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER = 0x2800;
    private const uint GL_TEXTURE_WRAP_S = 0x2802;
    private const uint GL_TEXTURE_WRAP_T = 0x2803;
    private const int GL_LINEAR = 0x2601;
    private const int GL_CLAMP_TO_EDGE = 0x812F;
    private const uint GL_UNPACK_ROW_LENGTH = 0x0CF2;
    private const int GL_RGBA8 = 0x8058;
    private const uint GL_RGBA = 0x1908;
    private const uint GL_FRAGMENT_SHADER = 0x8B30;
    private const uint GL_VERTEX_SHADER = 0x8B31;
    private const uint GL_COMPILE_STATUS = 0x8B81;
    private const uint GL_LINK_STATUS = 0x8B82;

    private delegate* unmanaged<uint, byte*> _glGetString;
    private delegate* unmanaged<uint, int*, void> _glGetIntegerv;
    private delegate* unmanaged<uint, byte> _glIsEnabled;
    private delegate* unmanaged<uint, void> _glEnable;
    private delegate* unmanaged<uint, void> _glDisable;
    private delegate* unmanaged<uint, void> _glActiveTexture;
    private delegate* unmanaged<uint, uint, void> _glBindTexture;
    private delegate* unmanaged<int, uint*, void> _glGenTextures;
    private delegate* unmanaged<int, uint*, void> _glDeleteTextures;
    private delegate* unmanaged<uint, uint, int, void> _glTexParameteri;
    private delegate* unmanaged<uint, int, void> _glPixelStorei;
    private delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> _glTexImage2D;
    private delegate* unmanaged<uint, uint> _glCreateShader;
    private delegate* unmanaged<uint, int, byte**, int*, void> _glShaderSource;
    private delegate* unmanaged<uint, void> _glCompileShader;
    private delegate* unmanaged<uint, uint, int*, void> _glGetShaderiv;
    private delegate* unmanaged<uint, int, int*, byte*, void> _glGetShaderInfoLog;
    private delegate* unmanaged<uint> _glCreateProgram;
    private delegate* unmanaged<uint, uint, void> _glAttachShader;
    private delegate* unmanaged<uint, uint, void> _glDetachShader;
    private delegate* unmanaged<uint, void> _glLinkProgram;
    private delegate* unmanaged<uint, uint, int*, void> _glGetProgramiv;
    private delegate* unmanaged<uint, int, int*, byte*, void> _glGetProgramInfoLog;
    private delegate* unmanaged<uint, void> _glDeleteShader;
    private delegate* unmanaged<uint, void> _glDeleteProgram;
    private delegate* unmanaged<uint, void> _glUseProgram;
    private delegate* unmanaged<uint, byte*, int> _glGetUniformLocation;
    private delegate* unmanaged<uint, byte*, int> _glGetAttribLocation;
    private delegate* unmanaged<int, int, void> _glUniform1i;
    private delegate* unmanaged<int, int, byte, float*, void> _glUniformMatrix4fv;
    private delegate* unmanaged<int, uint*, void> _glGenBuffers;
    private delegate* unmanaged<int, uint*, void> _glDeleteBuffers;
    private delegate* unmanaged<uint, uint, void> _glBindBuffer;
    private delegate* unmanaged<uint, nint, void*, uint, void> _glBufferData;
    private delegate* unmanaged<int, uint*, void> _glGenVertexArrays;
    private delegate* unmanaged<int, uint*, void> _glDeleteVertexArrays;
    private delegate* unmanaged<uint, void> _glBindVertexArray;
    private delegate* unmanaged<uint, void> _glEnableVertexAttribArray;
    private delegate* unmanaged<uint, int, uint, byte, int, nint, void> _glVertexAttribPointer;
    private delegate* unmanaged<int, int, int, int, void> _glViewport;
    private delegate* unmanaged<int, int, int, int, void> _glScissor;
    private delegate* unmanaged<uint, void> _glBlendEquation;
    private delegate* unmanaged<uint, uint, void> _glBlendEquationSeparate;
    private delegate* unmanaged<uint, uint, uint, uint, void> _glBlendFuncSeparate;
    private delegate* unmanaged<uint, int, uint, void*, void> _glDrawElements;
    private delegate* unmanaged<uint, int, uint, void*, int, void> _glDrawElementsBaseVertex;

    private uint _shaderProgram;
    private int _attribLocationTex;
    private int _attribLocationProjMtx;
    private uint _attribLocationVtxPos;
    private uint _attribLocationVtxUV;
    private uint _attribLocationVtxColor;
    private uint _vboHandle;
    private uint _eboHandle;
    private uint _vaoHandle;
    private bool _isGles;
    private bool _initialized;

    public void Initialize(IClyde clyde)
    {
        if (_initialized)
            return;

        LoadFunctions(clyde);

        var versionPtr = _glGetString(GL_VERSION);
        var versionStr = versionPtr != null ? Marshal.PtrToStringUTF8((nint)versionPtr) ?? "" : "";
        _isGles = versionStr.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);

        var io = Hexa.NET.ImGui.ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        if (!_isGles && _glDrawElementsBaseVertex != null)
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceObjects();
        _initialized = true;
    }

    private void LoadFunctions(IClyde clyde)
    {
        var clydeType = clyde.GetType();
        var bindingsField = clydeType.GetField("_glBindingsContext", BindingFlags.Instance | BindingFlags.NonPublic);
        var bindingsCtx = bindingsField?.GetValue(clyde)
            ?? throw new InvalidOperationException("Failed to access Clyde._glBindingsContext");
        var getProcMethod = bindingsCtx.GetType().GetMethod("GetProcAddress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Failed to find GetProcAddress on _glBindingsContext");

        nint Load(string name, bool optional = false)
        {
            var ptr = (nint)getProcMethod.Invoke(bindingsCtx, [name])!;
            if (ptr == IntPtr.Zero && !optional)
                throw new InvalidOperationException($"Failed to load OpenGL entry point '{name}'");
            return ptr;
        }

        _glGetString = (delegate* unmanaged<uint, byte*>)Load("glGetString");
        _glGetIntegerv = (delegate* unmanaged<uint, int*, void>)Load("glGetIntegerv");
        _glIsEnabled = (delegate* unmanaged<uint, byte>)Load("glIsEnabled");
        _glEnable = (delegate* unmanaged<uint, void>)Load("glEnable");
        _glDisable = (delegate* unmanaged<uint, void>)Load("glDisable");
        _glActiveTexture = (delegate* unmanaged<uint, void>)Load("glActiveTexture");
        _glBindTexture = (delegate* unmanaged<uint, uint, void>)Load("glBindTexture");
        _glGenTextures = (delegate* unmanaged<int, uint*, void>)Load("glGenTextures");
        _glDeleteTextures = (delegate* unmanaged<int, uint*, void>)Load("glDeleteTextures");
        _glTexParameteri = (delegate* unmanaged<uint, uint, int, void>)Load("glTexParameteri");
        _glPixelStorei = (delegate* unmanaged<uint, int, void>)Load("glPixelStorei");
        _glTexImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)Load("glTexImage2D");
        _glCreateShader = (delegate* unmanaged<uint, uint>)Load("glCreateShader");
        _glShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Load("glShaderSource");
        _glCompileShader = (delegate* unmanaged<uint, void>)Load("glCompileShader");
        _glGetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)Load("glGetShaderiv");
        _glGetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Load("glGetShaderInfoLog");
        _glCreateProgram = (delegate* unmanaged<uint>)Load("glCreateProgram");
        _glAttachShader = (delegate* unmanaged<uint, uint, void>)Load("glAttachShader");
        _glDetachShader = (delegate* unmanaged<uint, uint, void>)Load("glDetachShader", optional: true);
        _glLinkProgram = (delegate* unmanaged<uint, void>)Load("glLinkProgram");
        _glGetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)Load("glGetProgramiv");
        _glGetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Load("glGetProgramInfoLog");
        _glDeleteShader = (delegate* unmanaged<uint, void>)Load("glDeleteShader");
        _glDeleteProgram = (delegate* unmanaged<uint, void>)Load("glDeleteProgram");
        _glUseProgram = (delegate* unmanaged<uint, void>)Load("glUseProgram");
        _glGetUniformLocation = (delegate* unmanaged<uint, byte*, int>)Load("glGetUniformLocation");
        _glGetAttribLocation = (delegate* unmanaged<uint, byte*, int>)Load("glGetAttribLocation");
        _glUniform1i = (delegate* unmanaged<int, int, void>)Load("glUniform1i");
        _glUniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)Load("glUniformMatrix4fv");
        _glGenBuffers = (delegate* unmanaged<int, uint*, void>)Load("glGenBuffers");
        _glDeleteBuffers = (delegate* unmanaged<int, uint*, void>)Load("glDeleteBuffers");
        _glBindBuffer = (delegate* unmanaged<uint, uint, void>)Load("glBindBuffer");
        _glBufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)Load("glBufferData");

        var genVao = Load("glGenVertexArrays", optional: true);
        if (genVao == IntPtr.Zero) genVao = Load("glGenVertexArraysOES");
        _glGenVertexArrays = (delegate* unmanaged<int, uint*, void>)genVao;

        var delVao = Load("glDeleteVertexArrays", optional: true);
        if (delVao == IntPtr.Zero) delVao = Load("glDeleteVertexArraysOES");
        _glDeleteVertexArrays = (delegate* unmanaged<int, uint*, void>)delVao;

        var bindVao = Load("glBindVertexArray", optional: true);
        if (bindVao == IntPtr.Zero) bindVao = Load("glBindVertexArrayOES");
        _glBindVertexArray = (delegate* unmanaged<uint, void>)bindVao;

        _glEnableVertexAttribArray = (delegate* unmanaged<uint, void>)Load("glEnableVertexAttribArray");
        _glVertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, nint, void>)Load("glVertexAttribPointer");
        _glViewport = (delegate* unmanaged<int, int, int, int, void>)Load("glViewport");
        _glScissor = (delegate* unmanaged<int, int, int, int, void>)Load("glScissor");
        _glBlendEquation = (delegate* unmanaged<uint, void>)Load("glBlendEquation");
        _glBlendEquationSeparate = (delegate* unmanaged<uint, uint, void>)Load("glBlendEquationSeparate");
        _glBlendFuncSeparate = (delegate* unmanaged<uint, uint, uint, uint, void>)Load("glBlendFuncSeparate");
        _glDrawElements = (delegate* unmanaged<uint, int, uint, void*, void>)Load("glDrawElements");
        _glDrawElementsBaseVertex = (delegate* unmanaged<uint, int, uint, void*, int, void>)Load("glDrawElementsBaseVertex", optional: true);
    }

    private int GetInteger(uint pname)
    {
        int val = 0;
        _glGetIntegerv(pname, &val);
        return val;
    }

    private int GetUniformLocation(uint program, ReadOnlySpan<byte> nameUtf8)
    {
        fixed (byte* p = nameUtf8)
            return _glGetUniformLocation(program, p);
    }

    private int GetAttribLocation(uint program, ReadOnlySpan<byte> nameUtf8)
    {
        fixed (byte* p = nameUtf8)
            return _glGetAttribLocation(program, p);
    }

    private void CreateDeviceObjects()
    {
        uint lastTexture = (uint)GetInteger(GL_TEXTURE_BINDING_2D);
        uint lastArrayBuffer = (uint)GetInteger(GL_ARRAY_BUFFER_BINDING);
        uint lastVertexArray = (uint)GetInteger(GL_VERTEX_ARRAY_BINDING);

        string vertexShaderSource = _isGles
            ? """
              #version 300 es
              precision highp float;
              layout (location = 0) in vec2 Position;
              layout (location = 1) in vec2 UV;
              layout (location = 2) in vec4 Color;
              uniform mat4 ProjMtx;
              out vec2 Frag_UV;
              out vec4 Frag_Color;
              void main()
              {
                  Frag_UV = UV;
                  Frag_Color = Color;
                  gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
              }
              """
            : """
              #version 330 core
              layout (location = 0) in vec2 Position;
              layout (location = 1) in vec2 UV;
              layout (location = 2) in vec4 Color;
              uniform mat4 ProjMtx;
              out vec2 Frag_UV;
              out vec4 Frag_Color;
              void main()
              {
                  Frag_UV = UV;
                  Frag_Color = Color;
                  gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
              }
              """;

        string fragmentShaderSource = _isGles
            ? """
              #version 300 es
              precision mediump float;
              uniform sampler2D Texture;
              in vec2 Frag_UV;
              in vec4 Frag_Color;
              layout (location = 0) out vec4 Out_Color;
              void main()
              {
                  Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
              }
              """
            : """
              #version 330 core
              uniform sampler2D Texture;
              in vec2 Frag_UV;
              in vec4 Frag_Color;
              layout (location = 0) out vec4 Out_Color;
              void main()
              {
                  Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
              }
              """;

        uint vertHandle = CompileShader(GL_VERTEX_SHADER, vertexShaderSource);
        uint fragHandle = CompileShader(GL_FRAGMENT_SHADER, fragmentShaderSource);

        _shaderProgram = _glCreateProgram();
        _glAttachShader(_shaderProgram, vertHandle);
        _glAttachShader(_shaderProgram, fragHandle);
        _glLinkProgram(_shaderProgram);

        int linkStatus = 0;
        _glGetProgramiv(_shaderProgram, GL_LINK_STATUS, &linkStatus);
        if (linkStatus == 0)
        {
            byte* logBuf = stackalloc byte[1024];
            int logLen = 0;
            _glGetProgramInfoLog(_shaderProgram, 1024, &logLen, logBuf);
            var log = Encoding.UTF8.GetString(logBuf, Math.Clamp(logLen, 0, 1024));
            MarseyLogger.Warn($"ImGuiGl3Renderer program link failed: {log}");
        }

        if (_glDetachShader != null)
        {
            _glDetachShader(_shaderProgram, vertHandle);
            _glDetachShader(_shaderProgram, fragHandle);
        }
        _glDeleteShader(vertHandle);
        _glDeleteShader(fragHandle);

        _attribLocationTex = GetUniformLocation(_shaderProgram, "Texture\0"u8);
        _attribLocationProjMtx = GetUniformLocation(_shaderProgram, "ProjMtx\0"u8);
        _attribLocationVtxPos = (uint)GetAttribLocation(_shaderProgram, "Position\0"u8);
        _attribLocationVtxUV = (uint)GetAttribLocation(_shaderProgram, "UV\0"u8);
        _attribLocationVtxColor = (uint)GetAttribLocation(_shaderProgram, "Color\0"u8);

        uint vbo = 0, ebo = 0, vao = 0;
        _glGenBuffers(1, &vbo);
        _glGenBuffers(1, &ebo);
        _glGenVertexArrays(1, &vao);
        _vboHandle = vbo;
        _eboHandle = ebo;
        _vaoHandle = vao;

        _glBindVertexArray(_vaoHandle);
        _glBindBuffer(GL_ARRAY_BUFFER, _vboHandle);
        _glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, _eboHandle);

        int stride = sizeof(ImDrawVert);
        _glEnableVertexAttribArray(_attribLocationVtxPos);
        _glEnableVertexAttribArray(_attribLocationVtxUV);
        _glEnableVertexAttribArray(_attribLocationVtxColor);

        _glVertexAttribPointer(_attribLocationVtxPos, 2, GL_FLOAT, 0, stride, 0);
        _glVertexAttribPointer(_attribLocationVtxUV, 2, GL_FLOAT, 0, stride, 8);
        _glVertexAttribPointer(_attribLocationVtxColor, 4, GL_UNSIGNED_BYTE, 1, stride, 16);

        _glBindTexture(GL_TEXTURE_2D, lastTexture);
        _glBindBuffer(GL_ARRAY_BUFFER, lastArrayBuffer);
        _glBindVertexArray(lastVertexArray);
    }

    private uint CompileShader(uint type, string source)
    {
        uint handle = _glCreateShader(type);
        var utf8 = Encoding.UTF8.GetBytes(source);
        fixed (byte* pSrc = utf8)
        {
            byte* p = pSrc;
            int len = utf8.Length;
            _glShaderSource(handle, 1, &p, &len);
        }
        _glCompileShader(handle);

        int status = 0;
        _glGetShaderiv(handle, GL_COMPILE_STATUS, &status);
        if (status == 0)
        {
            byte* logBuf = stackalloc byte[1024];
            int logLen = 0;
            _glGetShaderInfoLog(handle, 1024, &logLen, logBuf);
            var log = Encoding.UTF8.GetString(logBuf, Math.Clamp(logLen, 0, 1024));
            MarseyLogger.Warn($"ImGuiGl3Renderer shader compile failed: {log}");
        }
        return handle;
    }

    public void RenderDrawData(ImDrawDataPtr drawData)
    {
        if (!_initialized || drawData.IsNull || !drawData.Valid)
            return;

        int fbWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int fbHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (fbWidth <= 0 || fbHeight <= 0)
            return;

        // Snapshot Clyde's OpenGL state before modifying anything.
        uint lastActiveTexture = (uint)GetInteger(GL_ACTIVE_TEXTURE);
        _glActiveTexture(GL_TEXTURE0);
        uint lastProgram = (uint)GetInteger(GL_CURRENT_PROGRAM);
        uint lastTexture = (uint)GetInteger(GL_TEXTURE_BINDING_2D);
        uint lastArrayBuffer = (uint)GetInteger(GL_ARRAY_BUFFER_BINDING);
        uint lastVertexArray = (uint)GetInteger(GL_VERTEX_ARRAY_BINDING);

        int* lastViewport = stackalloc int[4];
        _glGetIntegerv(GL_VIEWPORT, lastViewport);

        int* lastScissorBox = stackalloc int[4];
        _glGetIntegerv(GL_SCISSOR_BOX, lastScissorBox);

        uint lastBlendSrcRgb = (uint)GetInteger(GL_BLEND_SRC_RGB);
        uint lastBlendDstRgb = (uint)GetInteger(GL_BLEND_DST_RGB);
        uint lastBlendSrcAlpha = (uint)GetInteger(GL_BLEND_SRC_ALPHA);
        uint lastBlendDstAlpha = (uint)GetInteger(GL_BLEND_DST_ALPHA);
        uint lastBlendEquationRgb = (uint)GetInteger(GL_BLEND_EQUATION_RGB);
        uint lastBlendEquationAlpha = (uint)GetInteger(GL_BLEND_EQUATION_ALPHA);

        bool lastEnableBlend = _glIsEnabled(GL_BLEND) != 0;
        bool lastEnableCullFace = _glIsEnabled(GL_CULL_FACE) != 0;
        bool lastEnableDepthTest = _glIsEnabled(GL_DEPTH_TEST) != 0;
        bool lastEnableStencilTest = _glIsEnabled(GL_STENCIL_TEST) != 0;
        bool lastEnableScissorTest = _glIsEnabled(GL_SCISSOR_TEST) != 0;
        bool lastFramebufferSrgb = !_isGles && _glIsEnabled(GL_FRAMEBUFFER_SRGB) != 0;

        try
        {
            if (drawData.Textures.Data != null)
            {
                for (int i = 0; i < drawData.Textures.Size; i++)
                {
                    var tex = drawData.Textures[i];
                    if (tex.Status != ImTextureStatus.Ok)
                        UpdateTexture(tex);
                }
            }

            if (drawData.CmdListsCount == 0)
                return;

            _glEnable(GL_BLEND);
            _glBlendEquation(GL_FUNC_ADD);
            _glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ONE_MINUS_SRC_ALPHA);
            _glDisable(GL_CULL_FACE);
            _glDisable(GL_DEPTH_TEST);
            _glDisable(GL_STENCIL_TEST);
            _glEnable(GL_SCISSOR_TEST);
            if (lastFramebufferSrgb)
                _glDisable(GL_FRAMEBUFFER_SRGB);

            _glViewport(0, 0, fbWidth, fbHeight);

            float left = drawData.DisplayPos.X;
            float right = drawData.DisplayPos.X + drawData.DisplaySize.X;
            float top = drawData.DisplayPos.Y;
            float bottom = drawData.DisplayPos.Y + drawData.DisplaySize.Y;

            float* orthoProjection = stackalloc float[16]
            {
                2.0f / (right - left), 0.0f, 0.0f, 0.0f,
                0.0f, 2.0f / (top - bottom), 0.0f, 0.0f,
                0.0f, 0.0f, -1.0f, 0.0f,
                (right + left) / (left - right), (top + bottom) / (bottom - top), 0.0f, 1.0f
            };

            _glUseProgram(_shaderProgram);
            _glUniform1i(_attribLocationTex, 0);
            _glUniformMatrix4fv(_attribLocationProjMtx, 1, 0, orthoProjection);

            _glBindVertexArray(_vaoHandle);
            _glBindBuffer(GL_ARRAY_BUFFER, _vboHandle);
            _glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, _eboHandle);

            Vector2 clipOff = drawData.DisplayPos;
            Vector2 clipScale = drawData.FramebufferScale;

            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                ImDrawListPtr cmdList = drawData.CmdLists[n];

                _glBufferData(
                    GL_ARRAY_BUFFER,
                    cmdList.VtxBuffer.Size * sizeof(ImDrawVert),
                    cmdList.VtxBuffer.Data,
                    GL_STREAM_DRAW);

                _glBufferData(
                    GL_ELEMENT_ARRAY_BUFFER,
                    cmdList.IdxBuffer.Size * sizeof(ushort),
                    cmdList.IdxBuffer.Data,
                    GL_STREAM_DRAW);

                for (int cmdIdx = 0; cmdIdx < cmdList.CmdBuffer.Size; cmdIdx++)
                {
                    ImDrawCmd cmd = cmdList.CmdBuffer[cmdIdx];
                    if (cmd.UserCallback != null)
                        continue;

                    Vector4 clipRect = new(
                        (cmd.ClipRect.X - clipOff.X) * clipScale.X,
                        (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y,
                        (cmd.ClipRect.Z - clipOff.X) * clipScale.X,
                        (cmd.ClipRect.W - clipOff.Y) * clipScale.Y);

                    if (clipRect.X >= fbWidth || clipRect.Y >= fbHeight || clipRect.Z < 0.0f || clipRect.W < 0.0f)
                        continue;

                    _glScissor(
                        (int)clipRect.X,
                        (int)(fbHeight - clipRect.W),
                        (int)(clipRect.Z - clipRect.X),
                        (int)(clipRect.W - clipRect.Y));

                    _glBindTexture(GL_TEXTURE_2D, (uint)cmd.GetTexID().Handle);

                    void* idxOffsetBytes = (void*)(nuint)(cmd.IdxOffset * sizeof(ushort));
                    if (!_isGles && _glDrawElementsBaseVertex != null && cmd.VtxOffset != 0)
                    {
                        _glDrawElementsBaseVertex(
                            GL_TRIANGLES,
                            (int)cmd.ElemCount,
                            GL_UNSIGNED_SHORT,
                            idxOffsetBytes,
                            (int)cmd.VtxOffset);
                    }
                    else
                    {
                        _glDrawElements(
                            GL_TRIANGLES,
                            (int)cmd.ElemCount,
                            GL_UNSIGNED_SHORT,
                            idxOffsetBytes);
                    }
                }
            }
        }
        finally
        {
            // Restore Clyde's OpenGL state.
            _glUseProgram(lastProgram);
            _glBindTexture(GL_TEXTURE_2D, lastTexture);
            _glActiveTexture(lastActiveTexture);
            _glBindVertexArray(lastVertexArray);
            _glBindBuffer(GL_ARRAY_BUFFER, lastArrayBuffer);

            _glBlendEquationSeparate(lastBlendEquationRgb, lastBlendEquationAlpha);
            _glBlendFuncSeparate(lastBlendSrcRgb, lastBlendDstRgb, lastBlendSrcAlpha, lastBlendDstAlpha);

            if (lastEnableBlend) _glEnable(GL_BLEND); else _glDisable(GL_BLEND);
            if (lastEnableCullFace) _glEnable(GL_CULL_FACE); else _glDisable(GL_CULL_FACE);
            if (lastEnableDepthTest) _glEnable(GL_DEPTH_TEST); else _glDisable(GL_DEPTH_TEST);
            if (lastEnableStencilTest) _glEnable(GL_STENCIL_TEST); else _glDisable(GL_STENCIL_TEST);
            if (lastEnableScissorTest) _glEnable(GL_SCISSOR_TEST); else _glDisable(GL_SCISSOR_TEST);
            if (lastFramebufferSrgb) _glEnable(GL_FRAMEBUFFER_SRGB);

            _glViewport(lastViewport[0], lastViewport[1], lastViewport[2], lastViewport[3]);
            _glScissor(lastScissorBox[0], lastScissorBox[1], lastScissorBox[2], lastScissorBox[3]);
        }
    }

    private void UpdateTexture(ImTextureDataPtr tex)
    {
        if (tex.Status == ImTextureStatus.WantCreate || tex.Status == ImTextureStatus.WantUpdates)
        {
            int lastUnpackRowLength = GetInteger(GL_UNPACK_ROW_LENGTH);
            _glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);

            uint glTexId = (uint)tex.GetTexID().Handle;
            if (tex.Status == ImTextureStatus.WantCreate || glTexId == 0)
            {
                uint newTex = 0;
                _glGenTextures(1, &newTex);
                glTexId = newTex;
                _glBindTexture(GL_TEXTURE_2D, glTexId);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                _glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
                tex.SetTexID(new ImTextureID(glTexId));
            }
            else
            {
                _glBindTexture(GL_TEXTURE_2D, glTexId);
            }

            _glTexImage2D(
                GL_TEXTURE_2D,
                0,
                GL_RGBA8,
                tex.Width,
                tex.Height,
                0,
                GL_RGBA,
                GL_UNSIGNED_BYTE,
                tex.GetPixels());

            _glPixelStorei(GL_UNPACK_ROW_LENGTH, lastUnpackRowLength);
            tex.SetStatus(ImTextureStatus.Ok);
        }
        else if (tex.Status == ImTextureStatus.WantDestroy)
        {
            uint glTexId = (uint)tex.GetTexID().Handle;
            if (glTexId != 0)
                _glDeleteTextures(1, &glTexId);

            tex.SetTexID(ImTextureID.Null);
            tex.SetStatus(ImTextureStatus.Destroyed);
        }
    }

    public void Dispose()
    {
        if (!_initialized)
            return;

        if (_vaoHandle != 0)
        {
            uint vao = _vaoHandle;
            _glDeleteVertexArrays(1, &vao);
        }
        if (_vboHandle != 0)
        {
            uint vbo = _vboHandle;
            _glDeleteBuffers(1, &vbo);
        }
        if (_eboHandle != 0)
        {
            uint ebo = _eboHandle;
            _glDeleteBuffers(1, &ebo);
        }
        if (_shaderProgram != 0)
        {
            _glDeleteProgram(_shaderProgram);
        }

        _vaoHandle = 0;
        _vboHandle = 0;
        _eboHandle = 0;
        _shaderProgram = 0;
        _initialized = false;
    }
}
