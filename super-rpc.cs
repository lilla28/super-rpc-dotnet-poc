using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

using ObjectDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.ObjectDescriptor>;
using FunctionDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.FunctionDescriptor>;
using ClassDescriptors = System.Collections.Generic.Dictionary<string, SuperRPC.ClassDescriptor>;

namespace SuperRPC;

record AsyncCallbackEntry(Task task, Action<object?> complete, Action<Exception> fail, Type? type);

public class SuperRPC
{
    private AsyncLocal<object?> _currentContextAsyncLocal = new AsyncLocal<object?>();
    public object? CurrentContext {
        get => _currentContextAsyncLocal.Value;
        set => _currentContextAsyncLocal.Value = value;
    }

    protected readonly Func<string> ObjectIDGenerator;
    protected IRPCChannel? Channel;

    private ObjectDescriptors? remoteObjectDescriptors;
    private FunctionDescriptors? remoteFunctionDescriptors;
    private ClassDescriptors? remoteClassDescriptors;
    private TaskCompletionSource<bool>? remoteDescriptorsReceived = null;

    // private readonly proxyObjectRegistry = new ProxyObjectRegistry();
    private readonly ObjectIdDictionary<string, Type, Func<string, object>?> proxyClassRegistry = new ObjectIdDictionary<string, Type, Func<string, object>?>();
    
    private readonly ObjectIdDictionary<string, object, ObjectDescriptor> hostObjectRegistry = new ObjectIdDictionary<string, object, ObjectDescriptor>();
    private readonly ObjectIdDictionary<string, Delegate, FunctionDescriptor> hostFunctionRegistry = new ObjectIdDictionary<string, Delegate, FunctionDescriptor>();
    private readonly ObjectIdDictionary<string, Type, ClassDescriptor> hostClassRegistry = new ObjectIdDictionary<string, Type, ClassDescriptor>();

    private int callId = 0;
    private readonly Dictionary<string, AsyncCallbackEntry> asyncCallbacks = new Dictionary<string, AsyncCallbackEntry>();

    public SuperRPC(Func<string> objectIdGenerator) {
        ObjectIDGenerator = objectIdGenerator;
    }

    public void Connect(IRPCChannel channel) {
        Channel = channel;
        if (channel is IRPCReceiveChannel receiveChannel) {
            receiveChannel.MessageReceived += MessageReceived;
        }
    }

    public void RegisterHostObject(string objId, object target, ObjectDescriptor descriptor) {
        hostObjectRegistry.Add(objId, target, descriptor);
    }

    public void RegisterHostFunction(string objId, Delegate target, FunctionDescriptor? descriptor = null) {
        hostFunctionRegistry.Add(objId, target, descriptor ?? new FunctionDescriptor());
    }

    public void RegisterHostClass(string classId, Type clazz, ClassDescriptor descriptor) {
        descriptor.ClassId = classId;
        if (descriptor.Static is not null) {
            RegisterHostObject(classId, clazz, descriptor.Static);
        }

        hostClassRegistry.Add(classId, clazz, descriptor);
    }

    public void RegisterHostClass<TClass>(string classId, ClassDescriptor descriptor) {
        RegisterHostClass(classId, typeof(TClass), descriptor);
    }

    public void RegisterProxyClass<TInterface>(string classId) {
        RegisterProxyClass(classId, typeof(TInterface));
    }

    public void RegisterProxyClass(string classId, Type ifType) {
        proxyClassRegistry.Add(classId, ifType, null);
    }

    private TaskCompletionSource replySent;

    protected void MessageReceived(object? sender, MessageReceivedEventArgs eventArgs) {
        var message = eventArgs.message;
        var replyChannel = eventArgs.replyChannel ?? Channel;
        CurrentContext = eventArgs.context;

        // Debug.WriteLine($"MessageReceived action={message.action}, {message}");

        if (message.rpc_marker != "srpc") return;   // TODO: throw?

        switch (message) {
            case RPC_GetDescriptorsMessage: SendRemoteDescriptors(replyChannel);
                break;
            case RPC_DescriptorsResultMessage descriptors:
                SetRemoteDescriptors(descriptors);
                if (remoteDescriptorsReceived is not null) {
                    remoteDescriptorsReceived.SetResult(true);
                    remoteDescriptorsReceived = null;
                }
                break;
            case RPC_AnyCallTypeFnCallMessage functionCall:
                CallTargetFunction(functionCall, replyChannel);
                break;
            case RPC_ObjectDiedMessage objectDied:
                hostObjectRegistry.RemoveById(objectDied.objId);
                break;
            case RPC_FnResultMessageBase fnResult: {
                if (fnResult.callType == FunctionReturnBehavior.Async) {
                    if (asyncCallbacks.TryGetValue(fnResult.callId, out var entry)) {
                        if (fnResult.success) {
                            var result = ProcessValueAfterDeserialization(fnResult.result, replyChannel, entry.type);
                            entry.complete(result);
                        } else {
                            entry.fail(new ArgumentException(fnResult.result?.ToString()));
                        }
                        asyncCallbacks.Remove(fnResult.callId);
                    }
                }
                break;
            }
            default: 
                throw new ArgumentException("Invalid message received");
        }
    }

    private T GetHostObject<T>(string objId, IDictionary<string, T> registry) {
        if (!registry.TryGetValue(objId, out var entry)) {
            throw new ArgumentException($"No object found with ID '{objId}'.");
        }
        return entry;
    }

    protected void CallTargetFunction(RPC_AnyCallTypeFnCallMessage message, IRPCChannel replyChannel) {
        replySent = new TaskCompletionSource();

        object? result = null;
        bool success = true;

        try {
            switch (message) {
                case RPC_PropGetMessage propGet: {
                    var entry = GetHostObject(message.objId, hostObjectRegistry.ById);
                    result = (entry.obj as Type ?? entry.obj.GetType()).GetProperty(propGet.prop)?.GetValue(entry.obj);
                    break;
                }
                case RPC_PropSetMessage propSet: {
                    var entry = GetHostObject(message.objId, hostObjectRegistry.ById);
                    var propInfo = (entry.obj as Type ?? entry.obj.GetType()).GetProperty(propSet.prop);
                    if (propInfo is null) {
                        throw new ArgumentException($"Could not find property '{propSet.prop}' on object '{propSet.objId}'.");
                    }
                    var argDescriptors = entry.value.ProxiedProperties?.FirstOrDefault(pd => pd.Name == propSet.prop)?.Set?.Arguments;
                    var argDescriptor = argDescriptors?.Length > 0 ? argDescriptors[0] : null;
                    var value = ProcessValueAfterDeserialization(propSet.args[0], replyChannel, propInfo.PropertyType, argDescriptor);
                    propInfo.SetValue(entry.obj, value);
                    break;
                }
                case RPC_RpcCallMessage methodCall: {
                    var entry = GetHostObject(message.objId, hostObjectRegistry.ById);
                    var method = (entry.obj as Type ?? entry.obj.GetType()).GetMethod(methodCall.prop);
                    var argDescriptors = entry.value.Functions?.FirstOrDefault(fd => fd.Name == methodCall.prop)?.Arguments;
                    if (method is null) {
                        throw new ArgumentException($"Method '{methodCall.prop}' not found on object '{methodCall.objId}'.");
                    }
                    var args = ProcessArgumentsAfterDeserialization(methodCall.args, replyChannel, method.GetParameters().Select(param => param.ParameterType).ToArray(), argDescriptors);
                    result = method.Invoke(entry.obj, args);
                    break;
                }
                case RPC_FnCallMessage fnCall: {
                    var entry = GetHostObject(message.objId, hostFunctionRegistry.ById);
                    var method = entry.obj.Method;
                    var args = ProcessArgumentsAfterDeserialization(
                        fnCall.args,
                        replyChannel,
                        method.GetParameters().Select(param => param.ParameterType).ToArray(),
                        entry.value?.Arguments);
                    result = entry.obj.DynamicInvoke(args);
                    break;
                }
                case RPC_CtorCallMessage ctorCall: {
                    var classId = message.objId;
                    if (!hostClassRegistry.ById.TryGetValue(classId, out var entry)) {
                        throw new ArgumentException($"No class found with ID '{classId}'.");
                    }
                    var method = entry.obj.GetConstructors()[0];
                    var args = ProcessArgumentsAfterDeserialization(
                        ctorCall.args,
                        replyChannel,
                        method.GetParameters().Select(param => param.ParameterType).ToArray(),
                        entry.value.Ctor?.Arguments);
                    result = method.Invoke(args);
                    break;
                }

                default:
                    throw new ArgumentException($"Invalid message received, action={message.action}");
            }
        } catch (Exception e) {
            success = false;
            result = e.ToString();
        }

        if (message.callType == FunctionReturnBehavior.Async) {
            void SendAsyncResult(bool success, object? result) {
                SendAsyncIfPossible(new RPC_AsyncFnResultMessage {
                    success = success,
                    result = result,
                    callId = message.callId
                }, replyChannel);
            }

            if (result is Task task) {
                SendResultOnTaskCompletion(task, SendAsyncResult, replyChannel);
            }  else {
                SendAsyncResult(success, ProcessValueBeforeSerialization(result, replyChannel));
            }
        } else if (message.callType == FunctionReturnBehavior.Sync) {
            SendSyncIfPossible(new RPC_SyncFnResultMessage {
                success = success,
                result = ProcessValueBeforeSerialization(result, replyChannel),
            }, replyChannel);
        }

        replySent.SetResult();
    }

    /**
    * Send a request to get the descriptors for the registered host objects from the other side.
    * Uses synchronous communication if possible and returns `true`/`false` based on if the descriptors were received.
    * If sync is not available, it uses async messaging and returns a Task.
    */
    public ValueTask<bool> RequestRemoteDescriptors() {
        if (Channel is IRPCSendSyncChannel syncChannel) {
            var response = syncChannel.SendSync(new RPC_GetDescriptorsMessage());
            if (response is RPC_DescriptorsResultMessage descriptors) {
                SetRemoteDescriptors(descriptors);
                return new ValueTask<bool>(true);
            }
        }

        if (Channel is IRPCSendAsyncChannel asyncChannel) {
            remoteDescriptorsReceived = new TaskCompletionSource<bool>();
            asyncChannel.SendAsync(new RPC_GetDescriptorsMessage());
            return new ValueTask<bool>(remoteDescriptorsReceived.Task);
        }

        return new ValueTask<bool>(false);
    }

    private void SetRemoteDescriptors(RPC_DescriptorsResultMessage response) {
        if (response.objects is not null) {
            this.remoteObjectDescriptors = response.objects;
        }
        if (response.functions is not null) {
            this.remoteFunctionDescriptors = response.functions;
        }
        if (response.classes is not null) {
            this.remoteClassDescriptors = response.classes;
        }
    }

    /**
    * Send the descriptors for the registered host objects to the other side.
    * If possible, the message is sent synchronously.
    * This is a "push" style message, for "pull" see [[requestRemoteDescriptors]].
    */
    public void SendRemoteDescriptors(IRPCChannel? replyChannel) {
        replyChannel ??= Channel;
        SendSyncIfPossible(new RPC_DescriptorsResultMessage {
            objects = GetLocalObjectDescriptors(),
            functions = hostFunctionRegistry.ById.ToDictionary(x => x.Key, x => x.Value.value),
            classes = hostClassRegistry.ById.ToDictionary(x => x.Key, x => x.Value.value),
        }, replyChannel);
    }

    private ObjectDescriptors GetLocalObjectDescriptors() {
        var descriptors = new ObjectDescriptors();

        foreach (var (key, entry) in hostObjectRegistry.ById) {
            if (entry.value is ObjectDescriptor objectDescriptor) {
                var props = new Dictionary<string, object>();
                if (objectDescriptor.ReadonlyProperties is not null) {
                    foreach (var prop in objectDescriptor.ReadonlyProperties) {
                        var value = entry.obj.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry.obj);
                        if (value is not null) props.Add(prop, value);
                    }
                }
                descriptors.Add(key, new ObjectDescriptorWithProps(objectDescriptor, props));
            }
        }

        return descriptors;
    }

    private object? SendSyncIfPossible(RPC_Message message, IRPCChannel? channel = null) {
        channel ??= Channel;

        if (channel is IRPCSendSyncChannel syncChannel) {
            return syncChannel.SendSync(message);
        } else if (channel is IRPCSendAsyncChannel asyncChannel) {
            asyncChannel.SendAsync(message);
        }
        return null;
    }

    
    private object? SendAsyncIfPossible(RPC_Message message, IRPCChannel? channel = null) {
        channel ??= Channel;

        if (channel is IRPCSendAsyncChannel asyncChannel) {
            asyncChannel.SendAsync(message);
        } else if (channel is IRPCSendSyncChannel syncChannel) {
            return syncChannel.SendSync(message);
        }
        return null;
    }

    private string RegisterLocalObj(object obj, ObjectDescriptor? descriptor = null) {
        descriptor ??= new ObjectDescriptor();

        if (hostObjectRegistry.ByObj.TryGetValue(obj, out var entry)) {
            return entry.id;
        }
        var objId = ObjectIDGenerator();
        hostObjectRegistry.Add(objId, obj, descriptor);
        return objId;
    }

    private string RegisterLocalFunc(Delegate obj, FunctionDescriptor descriptor) {
        if (hostFunctionRegistry.ByObj.TryGetValue(obj, out var entry)) {
            return entry.id;
        }
        var objId = ObjectIDGenerator();
        hostFunctionRegistry.Add(objId, obj, descriptor);
        return objId;
    }

    private Dictionary<Type, Func<object, Type, object>> deserializers = new Dictionary<Type, Func<object, Type, object>>();

    public void RegisterDeserializer(Type type, Func<object, Type, object> deserializer) {
        deserializers.Add(type, deserializer);
    }


    private void SendResultOnTaskCompletion(Task task, Action<bool, object?> sendResult, IRPCChannel? replyChannel) {
        if (task.GetType().IsGenericType) {
            replySent.Task.ContinueWith(_ => {
                task.ContinueWith(t => {
                    sendResult(!t.IsFaulted,
                        t.IsFaulted ? t.Exception?.ToString() :
                        ProcessValueBeforeSerialization(((dynamic)t).Result, replyChannel)
                    );
                });
            });
        } else {
            replySent.Task.ContinueWith(_ => {
                task.ContinueWith(t => { sendResult(!t.IsFaulted, null); });
            });
        }
    }

    private bool ProcessPropertyValuesBeforeSerialization(object obj, PropertyInfo[] properties, Dictionary<string, object?> propertyBag, IRPCChannel replyChannel) {
        var needToConvert = false;
        foreach (var propInfo in properties) {
            if (!propInfo.CanRead || propInfo.GetIndexParameters().Length > 0) continue;

            var value = propInfo.GetValue(obj);
            var newValue = ProcessValueBeforeSerialization(value, replyChannel);
            propertyBag.Add(propInfo.Name, newValue);

            if (value is null ? newValue is not null : !value.Equals(newValue)) {
                needToConvert = true;
            }
        }
        return needToConvert;
    }

    private object?[] ProcessArgumentsBeforeSerialization(object?[] args/* , Type[] parameterTypes */, FunctionDescriptor? func, IRPCChannel? replyChannel) {
        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            // var type = parameterTypes[i];
            args[i] = ProcessValueBeforeSerialization(arg, replyChannel);
        }
        return args;
    }

    private object? ProcessValueBeforeSerialization(object? obj, IRPCChannel? replyChannel) {
        if (obj is null) return obj;

        var objType = obj.GetType();
        const BindingFlags PropBindFlags = BindingFlags.Public | BindingFlags.Instance;

        if (obj is Task task) {
            string? objId = null;
            if (!hostObjectRegistry.ByObj.ContainsKey(obj)) {

                void SendResult(bool success, object? result) {
                    SendAsyncIfPossible(new RPC_AsyncFnResultMessage {
                        success = success,
                        result = result,
                        callId = objId
                    }, replyChannel);
                }

                SendResultOnTaskCompletion(task, SendResult, replyChannel);
            }
            objId = RegisterLocalObj(obj);
            return new RPC_Object(objId, null, "Promise");
        }

        if (hostClassRegistry.ByObj.TryGetValue(objType, out var entry)) {
            var descriptor = entry.value;
            var objId = RegisterLocalObj(obj, descriptor.Instance);
            var propertyBag = new Dictionary<string, object?>();

            if (descriptor.Instance?.ReadonlyProperties is not null) {
                var propertyInfos = descriptor.Instance.ReadonlyProperties.Select(prop => objType.GetProperty(prop, PropBindFlags)).ToArray();
                ProcessPropertyValuesBeforeSerialization(obj, propertyInfos, propertyBag, replyChannel);
            }
            return new RPC_Object(objId, propertyBag, entry.id);
        }

        if (obj is Delegate func) {
            var objId = RegisterLocalFunc(func, new FunctionDescriptor());
            return new RPC_Function(objId);
        }

        if (objType.IsClass && objType != typeof(string)) {
            var propertyInfos = objType.GetProperties(PropBindFlags);
            var propertyBag = new Dictionary<string, object?>();

            if (ProcessPropertyValuesBeforeSerialization(obj, propertyInfos, propertyBag, replyChannel)) {
                var objId = RegisterLocalObj(obj);
                return new RPC_Object(objId, propertyBag);
            }
        }

        return obj;
    }

    private object?[] ProcessArgumentsAfterDeserialization(object?[] args, IRPCChannel replyChannel, Type[] parameterTypes, ArgumentDescriptor[]? argumentDescriptors) {
        if (args.Length != parameterTypes.Length) {
            throw new ArgumentException($"Method argument number mismatch. Expected {parameterTypes.Length} and got {args.Length}.");
        }

        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            var descr = argumentDescriptors?.FirstOrDefault(ad => ad.idx == i || ad.idx is null);
            var type = parameterTypes[i];
            args[i] = ProcessValueAfterDeserialization(args[i], replyChannel, parameterTypes[i], descr);
        }

        return args;
    }

    private Func<object, Type, object>? GetDeserializer(Type type) {
        if (deserializers.TryGetValue(type, out var deserializer)) {
            return deserializer;
        } else if (deserializers.TryGetValue(typeof(object), out deserializer)) {
            return deserializer;
        }
        return null;
    }

    private static Action<object?> CreateSetResultDelegate<T>(dynamic source) {
        return (object? result) => source.SetResult((T)result);
    }

    private AsyncCallbackEntry CreateAsyncCallback(Type returnType) {
        dynamic source = typeof(TaskCompletionSource<>).MakeGenericType(returnType).GetConstructor(Type.EmptyTypes).Invoke(null);
        return new AsyncCallbackEntry(source.Task,
            (Action<object?>)GetType().GetMethod("CreateSetResultDelegate", BindingFlags.NonPublic | BindingFlags.Static)
                .MakeGenericMethod(returnType).Invoke(null, new object[] { source }),
            (Action<Exception>)((Exception ex) => source.SetException(ex)),
            returnType);
    }

    private object? ProcessValueAfterDeserialization(object? obj, IRPCChannel replyChannel, Type? type = null, ArgumentDescriptor? argDescriptor = null) {
        if (obj is null) {
            if (type?.IsValueType == true) {
                throw new ArgumentException("null cannot be passed as a value type");
            }
        } else {
            if (type is not null) {
                var objType = obj.GetType();

                var rpcObjDeserializer = GetDeserializer(typeof(RPC_Object));
                if (rpcObjDeserializer is not null) {
                    var rpcObj = rpcObjDeserializer(obj, typeof(RPC_Object)) as RPC_Object;
                    if (rpcObj is not null) {
                        if (rpcObj._rpc_type == "function") {
                            var proxyFunc = CreateProxyFunctionFromDelegateType(type, rpcObj.objId, replyChannel, argDescriptor ?? new FunctionDescriptor());
                            obj = proxyFunc;
                            objType = obj.GetType();
                        }
                        // Promise -> Task
                        if (rpcObj.classId == "Promise") {
                            if (asyncCallbacks.TryGetValue(rpcObj.objId, out var asyncEntry)) {
                                obj = asyncEntry.task;
                            } else {
                                var resultType = type == typeof(Task) ? typeof(object) : UnwrapTaskReturnType(type);
                                var asyncCallback = CreateAsyncCallback(resultType);
                                asyncCallbacks.Add(rpcObj.objId, asyncCallback);
                                obj = asyncCallback.task;
                            }
                            objType = obj.GetType();
                        }

                        // special cases for _rpc_type=object/function
                        if (proxyClassRegistry.ByObj.TryGetValue(type, out var proxyClassEntry)) {
                            var factory = proxyClassEntry.value;
                            if (factory is null) {
                                factory = GetProxyClassFactory(proxyClassEntry.id, replyChannel);
                                proxyClassRegistry.Add(proxyClassEntry.id, proxyClassEntry.obj, factory);
                            }
                            obj = factory(rpcObj.objId);
                            objType = obj.GetType();
                        }
                    }
                }

                // custom deserializers
                var deserializer = GetDeserializer(type);
                if (deserializer is not null) {
                    obj = deserializer(obj, type);
                }

                if (!objType.IsAssignableTo(type) && obj is IConvertible) {
                    obj = Convert.ChangeType(obj, type);
                }
            }
            
            // recursive call for Dictionary
            if (obj is IDictionary<string, object?> dict) {
                foreach (var (key, value) in dict) {
                    dict[key] = ProcessValueAfterDeserialization(value, replyChannel);
                }
            }
        }
        return obj;
    }


    private Delegate CreateVoidProxyFunction<TReturn>(string? objId, FunctionDescriptor? func, string action, IRPCChannel? replyChannel) {

        TReturn? ProxyFunction(string instanceObjId, object?[] args) {
            SendAsyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                action = action,
                callType = FunctionReturnBehavior.Void,
                objId = objId ?? instanceObjId,
                prop = func?.Name,
                args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
            }, replyChannel);
            return default;
        }

        return ProxyFunction;
    }

    private Delegate CreateSyncProxyFunction<TReturn>(string? objId, FunctionDescriptor? func, string action, IRPCChannel? replyChannel) {
        
        TReturn? ProxyFunction(string instanceObjId, object?[] args) {
            var response = (RPC_SyncFnResultMessage?)SendSyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                action = action,
                callType = FunctionReturnBehavior.Sync,
                objId = objId ?? instanceObjId,
                prop = func?.Name,
                args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
            }, replyChannel);
            if (response is null) {
                throw new ArgumentException($"No response received");
            }
            if (!response.success) {
                throw new ArgumentException(response.result?.ToString());
            }
            return (TReturn?)ProcessValueAfterDeserialization(response.result, replyChannel);
        }

        return ProxyFunction;
    }

    private Delegate CreateAsyncProxyFunction<TReturn>(string? objId, FunctionDescriptor? func, string action, IRPCChannel? replyChannel) {
        
        Task<TReturn?> ProxyFunction(string instanceObjId, object?[] args) {
            callId++;

            SendAsyncIfPossible(new RPC_AnyCallTypeFnCallMessage {
                action = action,
                callType = FunctionReturnBehavior.Async,
                callId = callId.ToString(),
                objId = objId ?? instanceObjId,
                prop = func?.Name,
                args = ProcessArgumentsBeforeSerialization(args, func, replyChannel)
            }, replyChannel);
            
            var asyncCallback = CreateAsyncCallback(UnwrapTaskReturnType(typeof(TReturn)));
            asyncCallbacks.Add(callId.ToString(), asyncCallback);

            return (Task<TReturn>)asyncCallback.task;
        }

        return ProxyFunction;
    }

    private Delegate CreateProxyFunction<TReturn>(
        string objId,
        FunctionDescriptor? descriptor,
        string action,
        FunctionReturnBehavior defaultCallType = FunctionReturnBehavior.Async,
        IRPCChannel? replyChannel = null)
    {
        replyChannel ??= Channel;
        var callType = descriptor?.Returns ?? defaultCallType;

        if (callType == FunctionReturnBehavior.Async && replyChannel is not IRPCSendAsyncChannel) callType = FunctionReturnBehavior.Sync;
        if (callType == FunctionReturnBehavior.Sync && replyChannel is not IRPCSendSyncChannel) callType = FunctionReturnBehavior.Async;

        return callType switch {
            FunctionReturnBehavior.Void => CreateVoidProxyFunction<TReturn>(objId, descriptor, action, replyChannel),
            FunctionReturnBehavior.Sync => CreateSyncProxyFunction<TReturn>(objId, descriptor, action, replyChannel),
            _ => CreateAsyncProxyFunction<TReturn>(objId, descriptor, action, replyChannel)
        };
    }

    private Delegate CreateProxyFunctionWithReturnType(
        Type returnType,
        string? objId,
        FunctionDescriptor? descriptor,
        string action,
        IRPCChannel? replyChannel = null,
        FunctionReturnBehavior defaultCallType = FunctionReturnBehavior.Async)
    {
        if (returnType == typeof(void)) {
            returnType = typeof(object);
        }
        return (Delegate)(typeof(SuperRPC))
            .GetMethod("CreateProxyFunction", BindingFlags.NonPublic | BindingFlags.Instance)
            .MakeGenericMethod(returnType)
            .Invoke(this, new object[] { objId, descriptor, action, defaultCallType, replyChannel });
    }

    private Delegate CreateDynamicWrapperMethod(string methodName, Delegate proxyFunction, Type[] paramTypes, Type returnType) {
        var dmethod = new DynamicMethod(methodName,
            returnType,
            paramTypes.Prepend(proxyFunction.Target.GetType()).ToArray(),
            proxyFunction.Target.GetType(), true);

        var il = dmethod.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);       // "this" (ref of the instance of the class generated for proxyFunction)

        il.Emit(OpCodes.Ldnull);        // "null" to the stack -> instanceObjId

        il.Emit(OpCodes.Ldc_I4, paramTypes.Length);
        il.Emit(OpCodes.Newarr, typeof(object));    //arr = new object[paramTypes.Length]

        for (var i = 0; i < paramTypes.Length; i++) {
            il.Emit(OpCodes.Dup);               // arr ref
            il.Emit(OpCodes.Ldc_I4, i);         // int32: idx
            il.Emit(OpCodes.Ldarg, i + 1);      // arg(i+1)
            if (paramTypes[i].IsValueType) {
                il.Emit(OpCodes.Box, paramTypes[i]);
            }
            il.Emit(OpCodes.Stelem_Ref);        // arr[idx] = arg
        }

        il.Emit(OpCodes.Call, proxyFunction.Method);
        if (returnType == typeof(void)) {
            il.Emit(OpCodes.Pop);
        }
        il.Emit(OpCodes.Ret);

        var delegateTypes = Expression.GetDelegateType(paramTypes.Append(returnType).ToArray());

        return dmethod.CreateDelegate(delegateTypes, proxyFunction.Target);
    }

    private static bool IsTaskType(Type type) {
        return type == typeof(Task) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>);
    }
    private static Type UnwrapTaskReturnType(Type type) {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>)) {
            type = type.GetGenericArguments()[0];
        }
        return type;
    }

    public T GetProxyFunction<T> (string objId, IRPCChannel? channel = null) where T: Delegate {
        channel ??= Channel;
        // get it from a registry

        if (remoteFunctionDescriptors?.TryGetValue(objId, out var descriptor) is not true) {
            throw new ArgumentException($"No object descriptor found with ID '{objId}'.");
        }

        // put it in registry
        return (T)CreateProxyFunctionFromDelegateType(typeof(T), objId, channel, descriptor);
    }
        
    private Delegate CreateProxyFunctionFromDelegateType(Type delegateType, string objId, IRPCChannel? channel, FunctionDescriptor descriptor) {
        var method = delegateType.GetMethod("Invoke");
        if (method is null) {
            throw new ArgumentException($"Given generic type is not a generic delegate ({delegateType.FullName})");
        }

        var funcParamTypes = method.GetParameters().Select(pi => pi.ParameterType).ToArray();
        var proxyFunc = CreateProxyFunctionWithReturnType(UnwrapTaskReturnType(method.ReturnType), objId, descriptor, "fn_call", channel);
        
        return CreateDynamicWrapperMethod(objId + "_" + descriptor.Name, proxyFunc, funcParamTypes, method.ReturnType);
    }

    public T GetProxyObject<T>(string objId, IRPCChannel? channel = null) {
        if (remoteObjectDescriptors?.TryGetValue(objId, out var descriptor) is not true) {
            throw new ArgumentException($"No descriptor found for object ID {objId}.");
        }
        var factory = CreateProxyClass(objId + ".class", typeof(T), descriptor, channel);
        return (T)factory(objId);
    }

    private ObjectIdDictionary<string, Type, Func<string, object>?>.Entry GetProxyClassEntry(string classId) {
        if (!proxyClassRegistry.ById.TryGetValue(classId, out var proxyClassEntry)) {
            throw new ArgumentException($"No proxy class interface registered with ID '{classId}'.");
        }
        return proxyClassEntry;
    }

    private Func<string, object> GetProxyClassFactory(string classId, IRPCChannel? channel = null) {
        var entry = GetProxyClassEntry(classId);
        if (entry.value is not null) {
            return entry.value;
        }
        
        var factory = CreateProxyClass(classId, channel);
        proxyClassRegistry.Add(classId, entry.obj, factory);
        return factory;
    }

    private Func<string, object> CreateProxyClass(string classId, IRPCChannel? channel = null) {
        if (remoteClassDescriptors?.TryGetValue(classId, out var descriptor) is not true) {
            throw new ArgumentException($"No class descriptor found with ID '{classId}'.");
        }

        var ifType = GetProxyClassEntry(classId).obj;
        return CreateProxyClass(classId, ifType, descriptor.Instance, channel);
    }

    private Func<string, object> CreateProxyClass(string classId, Type ifType, ObjectDescriptor descriptor, IRPCChannel? channel = null) {
        channel ??= Channel;

        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"SuperRPC_dynamic({Guid.NewGuid()})"), AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("MainModule");
        var typeBuilder = moduleBuilder.DefineType(classId,
                TypeAttributes.Public |
                TypeAttributes.Class |
                TypeAttributes.AutoClass |
                TypeAttributes.AnsiClass |
                TypeAttributes.BeforeFieldInit |
                TypeAttributes.AutoLayout,
                null, new[] { ifType });

        var objIdField = typeBuilder.DefineField("objId", typeof(string), FieldAttributes.Public | FieldAttributes.InitOnly);
        var proxyFunctionsField = typeBuilder.DefineField("proxyFunctions", typeof(Delegate[]), FieldAttributes.Public | FieldAttributes.InitOnly);

        var constructorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new [] { typeof(string), typeof(Delegate[]) });
        var ctorIL = constructorBuilder.GetILGenerator();

        // call base()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ConstructorInfo superConstructor = typeof(Object).GetConstructor(Type.EmptyTypes);
        ctorIL.Emit(OpCodes.Call, superConstructor);

        ctorIL.Emit(OpCodes.Ldarg_0);   // this
        ctorIL.Emit(OpCodes.Ldarg_1);   // objId ref
        ctorIL.Emit(OpCodes.Stfld, objIdField); // this.objId = arg1

        ctorIL.Emit(OpCodes.Ldarg_0);   // this
        ctorIL.Emit(OpCodes.Ldarg_2);   // proxyFunctions ref
        ctorIL.Emit(OpCodes.Stfld, proxyFunctionsField); // this.proxyFunctions = arg2
        
        ctorIL.Emit(OpCodes.Ret);

        var proxyFunctions = new List<Delegate>();
        var propertyMethods = new List<string>();

        // Properties
        var properties = ifType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var propertyInfo in properties) {
            var isProxied = true;

            var propDescriptor = descriptor?.ProxiedProperties?.FirstOrDefault(desc => desc.Name == propertyInfo.Name);    // TODO: camelCase <-> PascalCase ?
            if (propDescriptor is null) {
                if (descriptor?.ReadonlyProperties?.Contains(propertyInfo.Name) is not true) { // TODO: camelCase <-> PascalCase ?
                    throw new ArgumentException($"No property descriptor found for property '{propertyInfo.Name}' in class '{classId}'.");
                }
                isProxied = false;
            }

            var isGetOnly = propDescriptor?.ReadOnly ?? false;

            var propertyBuilder = typeBuilder.DefineProperty(propertyInfo.Name,
                PropertyAttributes.HasDefault,
                propertyInfo.PropertyType,
                null);

            // The property set and property get methods require a special
            // set of attributes.
            var getSetAttr = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig;
            
            // Define the "get" accessor method
            var getterName = "get_" + propertyInfo.Name;
            propertyMethods.Add(getterName);
            var getPropMthdBldr = typeBuilder.DefineMethod(getterName,
                getSetAttr,
                propertyInfo.PropertyType,
                Type.EmptyTypes);

            var getterIL = getPropMthdBldr.GetILGenerator();
            propertyBuilder.SetGetMethod(getPropMthdBldr);

            ILGenerator? setterIL = null;
            if (!isGetOnly) {
                // Define the "set" accessor method 
                var setterName = "set_" + propertyInfo.Name;
                propertyMethods.Add(setterName);
                var setPropMthdBldr = typeBuilder.DefineMethod(setterName,
                    getSetAttr,
                    null,
                    new [] { propertyInfo.PropertyType });

                setterIL = setPropMthdBldr.GetILGenerator();
                propertyBuilder.SetSetMethod(setPropMthdBldr);
            }

            if (isProxied) {
                GenerateILMethod(getterIL, objIdField, proxyFunctionsField, proxyFunctions.Count, Type.EmptyTypes, propertyInfo.PropertyType);
                proxyFunctions.Add(CreateProxyFunctionWithReturnType(UnwrapTaskReturnType(propertyInfo.PropertyType), null, 
                    propDescriptor?.Get ?? new FunctionDescriptor { Name = propDescriptor.Name }, "prop_get", channel));

                if (!isGetOnly) {
                    GenerateILMethod(setterIL, objIdField, proxyFunctionsField, proxyFunctions.Count, new [] { propertyInfo.PropertyType }, typeof(void));
                    proxyFunctions.Add(CreateProxyFunctionWithReturnType(typeof(void), null, 
                        propDescriptor?.Set ?? new FunctionDescriptor { Name = propDescriptor.Name }, "prop_set", channel));
                }
            } else {    // not proxied -> "readonly"
                // backing field
                var backingFieldBuilder = typeBuilder.DefineField("_" + propertyInfo.Name,
                    propertyInfo.PropertyType,
                    FieldAttributes.Private);

                getterIL.Emit(OpCodes.Ldarg_0);
                getterIL.Emit(OpCodes.Ldfld, backingFieldBuilder);
                getterIL.Emit(OpCodes.Ret);

                setterIL.Emit(OpCodes.Ldarg_0);
                setterIL.Emit(OpCodes.Ldarg_1);
                setterIL.Emit(OpCodes.Stfld, backingFieldBuilder);
                setterIL.Emit(OpCodes.Ret);
            }
        }

        // Methods
        var methods = ifType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var methodInfo in methods) {
            if (propertyMethods.Contains(methodInfo.Name)) continue;

            var funcDescriptor = descriptor?.Functions?.FirstOrDefault(desc => desc.Name == methodInfo.Name);    // TODO: camelCase <-> PascalCase ?
            if (funcDescriptor is null) {
                throw new ArgumentException($"No function descriptor found for method '{methodInfo.Name}' in class '{classId}'.");
            }

            var paramInfos = methodInfo.GetParameters();
            var paramTypes = paramInfos.Select(pi => pi.ParameterType).ToArray();

            var methodBuilder = typeBuilder.DefineMethod(methodInfo.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final,
                CallingConventions.HasThis,
                methodInfo.ReturnType,
                methodInfo.ReturnParameter.GetRequiredCustomModifiers(),
                methodInfo.ReturnParameter.GetOptionalCustomModifiers(),
                paramTypes,
                paramInfos.Select(pi => pi.GetRequiredCustomModifiers()).ToArray(),
                paramInfos.Select(pi => pi.GetOptionalCustomModifiers()).ToArray()
            );

            GenerateILMethod(methodBuilder.GetILGenerator(), objIdField, proxyFunctionsField, proxyFunctions.Count, paramTypes, methodInfo.ReturnType);
            proxyFunctions.Add(CreateProxyFunctionWithReturnType(UnwrapTaskReturnType(methodInfo.ReturnType), null, funcDescriptor, "method_call", channel));
        }

        var proxyFunctionsArr = proxyFunctions.ToArray();
        var type = typeBuilder.CreateType();
        object CreateInstance(string objId) {
            return Activator.CreateInstance(type, objId, proxyFunctionsArr);
        }
        return CreateInstance;
    }

    void GenerateILMethod(ILGenerator il, FieldBuilder objIdField, FieldBuilder proxyFunctionsField, int funcIdx, Type[] paramTypes, Type returnType) {
        il.Emit(OpCodes.Ldarg_0);                   // "this" (ref of this generated class)
        il.Emit(OpCodes.Ldfld, proxyFunctionsField);  // "this" (ref of object[] containing proxy functions)
        il.Emit(OpCodes.Ldc_I4, funcIdx);
        il.Emit(OpCodes.Ldelem_Ref);                // proxyFunction [Delegate] is on the stack now

        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, typeof(object));    //arr2 = new object[2]

        il.Emit(OpCodes.Dup);               // arr2
        il.Emit(OpCodes.Ldc_I4_0);          // 0
        il.Emit(OpCodes.Ldarg_0);           // "this" 
        il.Emit(OpCodes.Ldfld, objIdField); // push(this.objId)
        il.Emit(OpCodes.Stelem_Ref);        // arr2[0] = objId
        
        il.Emit(OpCodes.Dup);               // arr2
        il.Emit(OpCodes.Ldc_I4_1);          // 1
        
        il.Emit(OpCodes.Ldc_I4, paramTypes.Length);
        il.Emit(OpCodes.Newarr, typeof(object));    //arr1 = new object[paramTypes.Length]

        for (var i = 0; i < paramTypes.Length; i++) {
            il.Emit(OpCodes.Dup);               // arr ref
            il.Emit(OpCodes.Ldc_I4, i);         // int32: idx
            il.Emit(OpCodes.Ldarg, i + 1);      // arg(i+1)
            if (paramTypes[i].IsValueType) {
                il.Emit(OpCodes.Box, paramTypes[i]);
            }
            il.Emit(OpCodes.Stelem_Ref);        // arr1[idx] = arg
        }

        il.Emit(OpCodes.Stelem_Ref);    // arr2[1] = arr1

        il.Emit(OpCodes.Callvirt, typeof(Delegate).GetMethod("DynamicInvoke"));

        if (returnType == typeof(void)) {
            il.Emit(OpCodes.Pop);
        }

        il.Emit(OpCodes.Ret);
    }
}

