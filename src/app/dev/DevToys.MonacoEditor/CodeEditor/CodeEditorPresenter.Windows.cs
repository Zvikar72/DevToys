﻿#if __WINDOWS__

using System.Reflection;
using System.Text;
using DevToys.UI.Framework.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Uno.Extensions;
using Uno.Logging;
using Windows.Foundation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace DevToys.MonacoEditor;

/// <summary>
/// Provides a WebView that displays the Monaco Editor.
/// </summary>
public sealed partial class CodeEditorPresenter : UserControl, ICodeEditorPresenter
{
    private static readonly List<TypedEventHandler<WebView2, CoreWebView2WebMessageReceivedEventArgs>> handlers = new();

    private enum PropertyAction
    {
        Read = 0,
        Write = 1,
    }

    private struct WebMessage
    {
        public Guid Guid { get; set; }
    }

    private struct MethodWebMessage
    {
        public string Id { get; set; }
        public string Method { get; set; }
        public string Args { get; set; }
    }

    private struct PropertyWebMessage
    {
        public string Id { get; set; }
        public string Property { get; set; }
        public PropertyAction Action { get; set; }
        public string Value { get; set; }
    }

    private readonly ILogger? _logger;

    private readonly WebView2 _webView = new();

    public CodeEditorPresenter()
    {
        _logger = this.Log();

        // Fill the WebView2 with ControlFillColorInputActive.
        var controlFillColorInputActive = (Windows.UI.Color)Application.Current.Resources["ControlFillColorInputActive"];
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", $"{controlFillColorInputActive.R:X2}{controlFillColorInputActive.G:X2}{controlFillColorInputActive.B:X2}");

        Content = _webView;

        this.Visibility = Visibility.Collapsed;
        _webView.AllowFocusOnInteraction = true;
        _webView.CoreWebView2Initialized += WebView_CoreWebView2Initialized;
    }

    /// <inheritdoc />
    public event TypedEventHandler<ICodeEditorPresenter, CoreWebView2NewWindowRequestedEventArgs>? NewWindowRequested;

    /// <inheritdoc />
    public event TypedEventHandler<ICodeEditorPresenter, CoreWebView2NavigationStartingEventArgs>? NavigationStarting;

    /// <inheritdoc />
    public event TypedEventHandler<ICodeEditorPresenter, CoreWebView2DOMContentLoadedEventArgs>? DOMContentLoaded;

    /// <inheritdoc />
    public event TypedEventHandler<ICodeEditorPresenter, CoreWebView2NavigationCompletedEventArgs>? NavigationCompleted;

    /// <inheritdoc />
    public event AsyncTypedEventHandler<ICodeEditorPresenter, EventArgs>? DotNetObjectInjectionRequested;

    /// <inheritdoc />
    public new event TypedEventHandler<ICodeEditorPresenter, RoutedEventArgs>? GotFocus;

    /// <inheritdoc />
    public new event TypedEventHandler<ICodeEditorPresenter, RoutedEventArgs>? LostFocus;

    public async Task LaunchAsync()
    {
        await _webView.EnsureCoreWebView2Async();

        _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
        _webView.CoreWebView2.Settings.IsPinchZoomEnabled = false;
        _webView.CoreWebView2.Settings.IsSwipeNavigationEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        _webView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
        _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
#endif

        string path = Path.Combine(AppContext.BaseDirectory, "DevToys.MonacoEditor", "CodeEditor", "CodeEditor.Windows.html");
        if (!File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "CodeEditor", "CodeEditor.Windows.html");
            Debug.Assert(File.Exists(path));
        }

        string rootDirectory = Directory.GetParent(path)!.Parent!.FullName;

        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            hostName: "devtoys.local",
            folderPath: rootDirectory,
            CoreWebView2HostResourceAccessKind.Allow);

        var tcs = new TaskCompletionSource<bool>();
        _webView.CoreWebView2.DOMContentLoaded += OnDOMContentLoaded;
        _webView.Source = new Uri("https://devtoys.local/CodeEditor/CodeEditor.Windows.html");

        await tcs.Task;

        // Request to inject .NET web object into the web page.
        Guard.IsNotNull(DotNetObjectInjectionRequested);
        await DotNetObjectInjectionRequested.Invoke(this, EventArgs.Empty);

        try
        {
            LogCreatingMonacoEditor();

            await _webView.ExecuteScriptAsync("createMonacoEditor(\"https://devtoys.local/\", document.getElementById('container'));");

            LogMonacoEditorCreated();
        }
        catch (Exception e)
        {
            LogMonacoEditorFailedToBeCreated(e);
        }

        this.Visibility = Visibility.Visible;

        void OnDOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            _webView.CoreWebView2.DOMContentLoaded -= OnDOMContentLoaded;
            tcs.TrySetResult(true);
        }
    }

    public async Task InjectDotNetObjectToWebPageAsync<T>(string name, T pObject)
    {
        LogInjectingDotNetObject(name);

        var sb = new StringBuilder();
        sb.AppendLine($"EditorContext.getEditorForElement(document.getElementById('container')).{name} = {{");

        var methodsGuid = Guid.NewGuid();
        MethodInfo[] methodInfo = typeof(T).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var methods = new Dictionary<string, MethodInfo>(methodInfo.Length);
        foreach (MethodInfo method in methodInfo)
        {
            string functionName = $"{char.ToLower(method.Name[0])}{method.Name.Substring(1)}";
            sb.AppendLine($@"{functionName}: function() {{");
            sb.AppendLine($@"   let id = this._callbackIndex++;");
            sb.AppendLine($@"   window.chrome.webview.postMessage(");
            sb.AppendLine($@"       JSON.stringify(");
            sb.AppendLine($@"           {{");
            sb.AppendLine($@"               guid: ""{methodsGuid}"",");
            sb.AppendLine($@"               id: id,");
            sb.AppendLine($@"               method: ""{functionName}"",");
            sb.AppendLine($@"               args: JSON.stringify([...arguments])");
            sb.AppendLine($@"           }}));");
            sb.AppendLine($@"   const promise = new Promise((resolve, reject) => this._callbacks.set(id, {{ resolve: resolve, reject: reject }}));");
            sb.AppendLine($@"   return promise;");
            sb.AppendLine($@"}},");
            methods.Add($"{functionName}`{method.GetParameters().Length}", method);
        }

        var propertiesGuid = Guid.NewGuid();
        PropertyInfo[] propertyInfo = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var properties = new Dictionary<string, PropertyInfo>(propertyInfo.Length);
        foreach (PropertyInfo property in propertyInfo)
        {
            string propertyName = $"{char.ToLower(property.Name[0])}{property.Name.Substring(1)}";
            if (property.CanRead)
            {
                sb.AppendLine($@"get {propertyName}() {{");
                sb.AppendLine($@"   let id = this._callbackIndex++;");
                sb.AppendLine($@"   window.chrome.webview.postMessage(");
                sb.AppendLine($@"       JSON.stringify(");
                sb.AppendLine($@"           {{");
                sb.AppendLine($@"               guid: ""{propertiesGuid}"",");
                sb.AppendLine($@"               id: id,");
                sb.AppendLine($@"               property: ""{propertyName}"",");
                sb.AppendLine($@"               action: ""{(int)PropertyAction.Read}""");
                sb.AppendLine($@"           }}));");
                sb.AppendLine($@"   const promise = new Promise((resolve, reject) => this._callbacks.set(id, {{ resolve: resolve, reject: reject }}));");
                sb.AppendLine($@"   return promise;");
                sb.AppendLine($@"}},");
            }
            if (property.CanWrite)
            {
                sb.AppendLine($@"set {propertyName}(value) {{");
                sb.AppendLine($@"   let id = this._callbackIndex++;");
                sb.AppendLine($@"   window.chrome.webview.postMessage(");
                sb.AppendLine($@"       JSON.stringify(");
                sb.AppendLine($@"           {{");
                sb.AppendLine($@"               guid: ""{propertiesGuid}"",");
                sb.AppendLine($@"               id: id,");
                sb.AppendLine($@"               property: ""{propertyName}"",");
                sb.AppendLine($@"               action: ""{(int)PropertyAction.Write}"",");
                sb.AppendLine($@"               value: JSON.stringify(value)");
                sb.AppendLine($@"           }}));");
                sb.AppendLine($@"   const promise = new Promise((resolve, reject) => this._callbacks.set(id, {{ resolve: resolve, reject: reject }}));");
                sb.AppendLine($@"   return promise;");
                sb.AppendLine($@"}},");
            }
            properties[propertyName] = property;
        }

        // Add a map<int, (promiseAccept, promiseReject)> to the object used to resolve results
        sb.AppendLine($@"_callbacks: new Map(),");

        // And a shared counter to index into that map
        sb.AppendLine($@"_callbackIndex: 0,");

        sb.AppendLine("}");

        try
        {
            await _webView.ExecuteScriptAsync($"{sb}").AsTask();
            LogDotNetObjectInjected(name);
        }
        catch (Exception ex)
        {
            LogDotNetObjectInjectionFailed(name, ex);
        }

        async void Handler(WebView2 _, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string webMessageAsString = e.TryGetWebMessageAsString();

            WebMessage message = JsonConvert.DeserializeObject<WebMessage>(webMessageAsString);
            if (message.Guid == methodsGuid)
            {
                MethodWebMessage methodMessage = JsonConvert.DeserializeObject<MethodWebMessage>(webMessageAsString);
                object[] arguments = JsonConvert.DeserializeObject<object[]>(methodMessage.Args) ?? Array.Empty<object>();
                MethodInfo method = methods[$"{methodMessage.Method}`{arguments.Length}"];
                try
                {
                    object? result = method.Invoke(pObject, arguments);
                    if (result is not null)
                    {
                        Type resultType = result.GetType();
                        dynamic? task = null;
                        if (resultType.Name.StartsWith("TaskToAsyncOperationAdapter")
                            || resultType.IsInstanceOfType(typeof(IAsyncInfo)))
                        {
                            // Task that needs to be converted to a task first
                            if (resultType.GenericTypeArguments.Length > 0)
                            {
                                MethodInfo asTask = typeof(WindowsRuntimeSystemExtensions)
                                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                                    .Where(method => method.GetParameters().Length == 1
                                        && method.Name == "AsTask"
                                        && method.ToString()!.Contains("Windows.Foundation.Task`1[TResult]"))
                                    .First();

                                asTask = asTask.MakeGenericMethod(resultType.GenericTypeArguments[0]);
                                task = (Task)asTask.Invoke(null, new[] { result })!;
                            }
                            else
                            {
                                task = WindowsRuntimeSystemExtensions.AsTask((dynamic)result);
                            }
                        }
                        else
                        {
                            MethodInfo? awaiter = resultType.GetMethod(nameof(Task.GetAwaiter));
                            if (awaiter is not null)
                            {
                                task = (Task)result;
                            }
                        }
                        if (task is object)
                        {
                            result = await task;
                        }
                    }

                    string json = JsonConvert.SerializeObject(result);

                    var sb = new StringBuilder();
                    sb.AppendLine("(function() {");
                    sb.AppendLine($@"   let {name} = EditorContext.getEditorForElement(document.getElementById('container')).{name};");
                    sb.AppendLine($@"   {name}._callbacks.get({methodMessage.Id}).resolve(JSON.parse({json}));");
                    sb.AppendLine($@"   {name}._callbacks.delete({methodMessage.Id});");
                    sb.AppendLine("})();");
                    await _webView.ExecuteScriptAsync(sb.ToString());
                }
                catch (Exception ex)
                {
                    LogDotNetObjectInjectionCallbackMethodFailed(name, method.Name, ex);
                    string json = JsonConvert.SerializeObject(ex, new JsonSerializerSettings() { Error = (_, e) => e.ErrorContext.Handled = true });
                    await _webView.ExecuteScriptAsync($@"EditorContext.getEditorForElement(document.getElementById('container')).{name}._callbacks.get({methodMessage.Id}).reject(JSON.parse({json})); EditorContext.getEditorForElement(document.getElementById('container')).{name}._callbacks.delete({methodMessage.Id});");
                }
            }
            else if (message.Guid == propertiesGuid)
            {
                PropertyWebMessage propertyMessage = JsonConvert.DeserializeObject<PropertyWebMessage>(webMessageAsString);
                PropertyInfo property = properties[propertyMessage.Property];
                try
                {
                    object? result;
                    if (propertyMessage.Action == PropertyAction.Read)
                    {
                        result = property.GetValue(pObject);
                    }
                    else
                    {
                        object? value = JsonConvert.DeserializeObject(propertyMessage.Value, property.PropertyType);
                        property.SetValue(pObject, value);
                        result = new object();
                    }

                    string json = JsonConvert.SerializeObject(result);

                    var sb = new StringBuilder();
                    sb.AppendLine("(function() {");
                    sb.AppendLine($@"   let {name} = EditorContext.getEditorForElement(document.getElementById('container')).{name};");
                    sb.AppendLine($@"   {name}._callbacks.get({propertyMessage.Id}).resolve(JSON.parse({json}));");
                    sb.AppendLine($@"   {name}._callbacks.delete({propertyMessage.Id});");
                    sb.AppendLine("})();");
                    await _webView.ExecuteScriptAsync(sb.ToString());
                }
                catch (Exception ex)
                {
                    LogDotNetObjectInjectionCallbackPropertyFailed(name, property.Name, ex);
                    string json = JsonConvert.SerializeObject(ex, new JsonSerializerSettings() { Error = (_, e) => e.ErrorContext.Handled = true });
                    await _webView.ExecuteScriptAsync($@"EditorContext.getEditorForElement(document.getElementById('container')).{name}._callbacks.get({propertyMessage.Id}).reject(JSON.parse({json})); EditorContext.getEditorForElement(document.getElementById('container')).{name}._callbacks.delete({propertyMessage.Id});");
                }
            }
        }

        handlers.Add(Handler);
        _webView.WebMessageReceived += Handler;
    }

    public async Task<string> InvokeScriptAsync(string script)
    {
        script
            = $@"
                (function()
                {{
                    try {{
                        let result = (function()
                        {{
                            let editorContext = EditorContext.getEditorForElement(document.getElementById('container'));
                            {script}
                        }})();
                        return result;
                    }}
                    catch(err){{
                        let editorContext = EditorContext.getEditorForElement(document.getElementById('container'));
                        editorContext.Debug.log(err);
                    }}
                    finally {{
                    }}
                }})();";

        LogInvokingJavaScript();

        try
        {
            string result
                = await DispatcherQueue.RunOnUIThreadAsync(
                    DispatcherQueuePriority.High,
                    async () =>
                    {
                        return await _webView.ExecuteScriptAsync(script);
                    });

            LogInvokedJavaScriptSuccessfully(result);

            return result;
        }
        catch (Exception e)
        {
            LogFailedToInvokeJavaScript(e);

            throw;
        }
    }

    private void WebView_CoreWebView2Initialized(WebView2 sender, CoreWebView2InitializedEventArgs args)
    {
        _webView.CoreWebView2.NewWindowRequested += (wv, args) => NewWindowRequested?.Invoke(this, args);
        _webView.CoreWebView2.NavigationStarting += (wv, args) => NavigationStarting?.Invoke(this, args);
        _webView.CoreWebView2.DOMContentLoaded += (wv, args) => DOMContentLoaded?.Invoke(this, args);
        _webView.CoreWebView2.NavigationCompleted += (wv, args) => NavigationCompleted?.Invoke(this, args);
    }

    [LoggerMessage(0, LogLevel.Information, "{caller}: Creating Monaco Editor...")]
    partial void LogCreatingMonacoEditor([CallerMemberName] string? caller = null);

    [LoggerMessage(1, LogLevel.Information, "{caller}: Monaco Editor has been created successfully.")]
    partial void LogMonacoEditorCreated([CallerMemberName] string? caller = null);

    [LoggerMessage(2, LogLevel.Error, "{caller}: Failed to create the Monaco Editor.")]
    partial void LogMonacoEditorFailedToBeCreated(Exception exception, [CallerMemberName] string? caller = null);

    [LoggerMessage(3, LogLevel.Debug, "{caller}: Trying to inject .NET object '{name}' in web page...")]
    partial void LogInjectingDotNetObject(string name, [CallerMemberName] string? caller = null);

    [LoggerMessage(4, LogLevel.Debug, "{caller}: .NET object '{name}' injected successfully.")]
    partial void LogDotNetObjectInjected(string name, [CallerMemberName] string? caller = null);

    [LoggerMessage(5, LogLevel.Error, "{caller}: Failed to inject .NET object '{name}'.")]
    partial void LogDotNetObjectInjectionFailed(string name, Exception exception, [CallerMemberName] string? caller = null);

    [LoggerMessage(6, LogLevel.Error, "{caller}-CALLBACK: Exception in {name}.{methodName}.")]
    partial void LogDotNetObjectInjectionCallbackMethodFailed(string name, string methodName, Exception exception, [CallerMemberName] string? caller = null);

    [LoggerMessage(7, LogLevel.Error, "{caller}-CALLBACK: Exception in {name}.{propertyName}.")]
    partial void LogDotNetObjectInjectionCallbackPropertyFailed(string name, string propertyName, Exception exception, [CallerMemberName] string? caller = null);

    [LoggerMessage(8, LogLevel.Debug, "{caller}: Invoking JavaScript...")]
    partial void LogInvokingJavaScript([CallerMemberName] string? caller = null);

    [LoggerMessage(9, LogLevel.Debug, "{caller}: JavaScript invoked successfully: {result}")]
    partial void LogInvokedJavaScriptSuccessfully(string result, [CallerMemberName] string? caller = null);

    [LoggerMessage(10, LogLevel.Error, "{caller}: Failed to invoke JavaScript.")]
    partial void LogFailedToInvokeJavaScript(Exception exception, [CallerMemberName] string? caller = null);
}

#endif