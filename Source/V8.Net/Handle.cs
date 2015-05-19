﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text;

#if !(V1_1 || V2 || V3 || V3_5)
using System.Dynamic;
#endif

namespace V8.Net
{
    // ========================================================================================================================

    public partial class V8Engine
    {
#if DEBUG && TRACKHANDLES
        /// <summary>
        /// Holds all the InternalHandle values that were set with native proxy handles.
        /// <para>Note: This list is only available when compiling with the DEBUG and TRACKHANDLES compiler directives.</para>
        /// </summary>
        public static readonly List<InternalHandle> AllInternalHandlesEverCreated = new List<InternalHandle>();
#endif
    }

    /// <summary>
    /// Wrapper to an InternalHandle value for GC tracking purposes.  Calls work internally using 'InternalHandle' for
    /// efficiency; however, an object handle is needed outside of V8.Net for end users. To prevent creating many objects
    /// for the same native V8 handle, 'Handle' objects are stored in a fixed size array for quick lookup, and are shared
    /// across many InternalHandle values. When all InternalHandle values are gone, this handle gets collected, unless
    /// also referenced.
    /// </summary>
    public class Handle : IV8Disposable, IHandleBased
    {
        public static readonly Handle Empty = new Handle(null);

        internal InternalHandle _Handle;

        protected Handle() { }
        internal Handle(InternalHandle h)
        {
            System.Diagnostics.Debug.Assert(h._Object != null, "'h._Object' must be null - not allowed to overwrite existing object references on handles.");
            h._Object = this;
        }

        /// <summary>
        /// This is called on the GC finalizer thread to flag that this managed object entry can be collected.
        /// <para>Note: There are no longer any managed references to the object at this point; HOWEVER, there may still be NATIVE ones.
        /// This means the object may survive this process, at which point it's up to the worker thread to clean it up when the native V8 GC is ready.</para>
        /// </summary>
        ~Handle()
        {
            this.Finalizing();
        }

        public V8Engine Engine
        {
            get { return _Handle._Engine; }
        }

        public bool CanDispose
        {
            get { return true; }
        }

        public void Dispose()
        {
            _Handle.Dispose();
        }

        public virtual InternalHandle InternalHandle
        {
            get { return _Handle; }
            set { throw new NotSupportedException("Not allowed to change the underlying internal handle directly on a 'Handle' object. Derived types can override this to provide a setter where supported."); }
        }

        public new V8NativeObject Object
        {
            get { return _Handle.Object; }
        }

        public static implicit operator InternalHandle(Handle h) { return h._Handle; }
        public static InternalHandle operator ~(Handle h) { return h._Handle; }
    }

    /// <summary>
    /// Keeps track of native V8 handles (C++ native side).
    /// <para>DO NOT STORE THIS HANDLE. Use "Handle" instead (i.e. "Handle h = someInternalHandle;"), or use the value with the "using(someInternalHandle){}" statement.</para>
    /// </summary>
    public unsafe struct InternalHandle :
        IHandle, IHandleBased,
        IV8Object,
        IBasicHandle, // ('IDisposable' will not box in a "using" statement: http://stackoverflow.com/questions/2412981/if-my-struct-implements-idisposable-will-it-be-boxed-when-used-in-a-using-statem)
        IDynamicMetaObjectProvider
    {
        // --------------------------------------------------------------------------------------------------------------------

        public static readonly InternalHandle Empty = new InternalHandle((HandleProxy*)null);

        // --------------------------------------------------------------------------------------------------------------------

        internal HandleProxy* _HandleProxy; // (the native proxy struct wrapped by this instance)

        /// <summary>
        /// The managed object represented by this handle, if any, or null otherwise.
        /// If this handle does not represent a managed object, then this may be set to a 'Handle' instead to allow tracking 
        /// and disposing the internal handle value within external user code.
        /// </summary>
        internal Handle _Object;

        /// <summary>
        /// If this is true, this handle cannot be disposed.  Handles are usually only locked on V8NativeObject instances to
        /// prevent disposing them, as there is a native V8 handle to an underlying JavaScript object that must be considered
        /// during the disposal process.
        /// </summary>
        internal bool _locked;

        /// <summary>
        /// InternalHandle values are disposed within the engine automatically.  If a handle is to be used outside the engine,
        /// this should be called to allow the handle to be tracked by the managed GC.
        /// <para>Note: This is usually called when returning handles from public methods.  This is not called on handles
        /// passed into callbacks, as those handles are disposed automatically upon return from the callback.  This method must 
        /// be called to prevent any internal handle from being dispose in such cases.</para>
        /// </summary>
        public InternalHandle KeepAlive()
        {
            return _Object ?? (_Object = new Handle(this));
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Wraps a given native handle proxy to provide methods to operate on it.
        /// </summary>
        internal InternalHandle(HandleProxy* hp)
        {
            _HandleProxy = null;
            _Object = null;
            _Set(hp);
        }

        /// <summary>
        /// Sets this instance to the same specified handle value.
        /// </summary>
        public InternalHandle(InternalHandle handle)
        {
            _HandleProxy = null;
            _Object = null;
            _Set(handle);
        }

        /// <summary>
        /// Bypasses checking if 'handle._First' is set.
        /// </summary>
        internal InternalHandle _Set(HandleProxy* hp, bool checkIfFirst = true)
        {
            if (_HandleProxy != hp)
            {
                if (_HandleProxy != null)
                    Dispose();

                _HandleProxy = hp;

                if (_HandleProxy != null)
                {
                    // ... verify the native handle proxy ID is within a valid range before storing it, and resize as needed ...

                    var engine = V8Engine._Engines[_HandleProxy->EngineID];
                    var handleID = _HandleProxy->ID;
                    var currentHandleProxies = engine._HandleProxies;

                    if (handleID >= currentHandleProxies.Length)
                        lock (engine._HandleProxies)
                        {
                            // ... need to resize the array ...
                            HandleProxy*[] _newHandleProxiesArray = new HandleProxy*[(100 + handleID) * 2];
                            Array.Copy(currentHandleProxies, _newHandleProxiesArray, currentHandleProxies.Length);
                            //?Marshal.Copy((IntPtr)intcurrentHandleProxies, 0, (IntPtr)_newHandleProxiesArray, IntPtr.Size * currentHandleProxies.Length);
                            engine._HandleProxies = currentHandleProxies = _newHandleProxiesArray;
                        }

                    currentHandleProxies[handleID] = _HandleProxy;

                    _HandleProxy->ManagedReference = 1;

                    GC.AddMemoryPressure((Marshal.SizeOf(typeof(HandleProxy)))); // (many handle instances can share one proxy)

                    _HandleProxy->ManagedReference++;

#if DEBUG && TRACKHANDLES
                    V8Engine.AllInternalHandlesEverCreated.Add(this);
#endif
                }
            }

            return this;
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns true if this handle is directly accessed on a V8NativeObject object.  Such handles are disposed under a
        /// controlled process that must synchronize across both V8 and V8.Net environments (native and managed sides).
        /// </summary>
        public bool IsLocked
        {
            get
            {
                unsafe
                {
                    // ... this little trick uses the wrapped unsafe pointer to determine if this value exists directly
                    // on the underlying object, which is the case if the pointer to the proxy pointer is that same 
                    // between this value, and the one on the target object ...
                    fixed (void* ptr1 = &this._HandleProxy, ptr2 = &((V8NativeObject)_Object)._Handle._HandleProxy)
                    {
                        return ptr1 != ptr2; // (handle proxy pointers are copies, so this handle value can be cleared)
                    }
                }
            }
        }

        /// <summary>
        /// Returns true if calling 'Dispose()' will release the native side handle immediately on the native side.  Any handle
        /// can usually be disposed, except the only handle left on a managed object. In this case, false is returned, since 
        /// the managed object is responsible for disposing it in a controlled manner coordinated with V8.
        /// </summary>
        public bool CanDispose
        {
            get
            {
                return _Object == null || !IsLocked;
            }
        }

        /// <summary>
        /// Disposes of this handle. If the handle cannot be disposed, it is cleared instead.
        /// If the handle represents a managed V8NativeObject instance, the handle cannot be disposed externally. Managed objects
        /// will begin a disposal process when there are no more managed references. When this occurs, the native side V8 handle
        /// is made "weak".  When there are no more references in V8, the V8's GC calls back into the managed side to notify
        /// that disposal can complete. In all other cases, disposing a handle will succeed and simply clears it, making it empty.
        /// </summary>
        public void Dispose()
        {
            if (CanDispose)
            {
                _HandleProxy->IsBeingDisposed = true; // (sets '__HandleProxy->Disposed' to 1)
                _HandleProxy->ManagedReference = 0;

                V8NetProxy.DisposeHandleProxy(_HandleProxy); // (note: this will not work unless '__HandleProxy->Disposed' is 1, which starts the disposal process)

                _CurrentObjectID = -1;

                GC.RemoveMemoryPressure((Marshal.SizeOf(typeof(HandleProxy))));
            }
            else if (IsLocked)
                throw new InvalidOperationException("The handle is locked and cannot be disposed. Locked handles belong to 'V8NativeObject' objects, which are responsible for disposing them under controlled conditions.");

            _HandleProxy = null;
            _Object = null;
            _locked = false;
        }

        /// <summary>
        /// Returns true if this handle is disposed (no longer in use).  Disposed native proxy handles are kept in a cache for performance reasons.
        /// </summary>
        public bool IsDisposed { get { return _HandleProxy == null || _HandleProxy->IsDisposed; } }

        // --------------------------------------------------------------------------------------------------------------------

        public static implicit operator Handle(InternalHandle handle)
        {
            return handle._HandleProxy == null ? Handle.Empty : handle._Object is Handle ? (Handle)handle._Object : new Handle(handle);
        }

        public static implicit operator ObjectHandle(InternalHandle handle)
        {
            if (!handle.IsEmpty && !handle.IsObjectType) // (note: an empty handle is ok)
                throw new InvalidCastException(string.Format(_VALUE_NOT_AN_OBJECT_ERRORMSG, handle));
            return handle._HandleProxy != null ? new ObjectHandle(handle) : ObjectHandle.Empty;
        }

        public static implicit operator V8NativeObject(InternalHandle handle)
        {
            return handle.Object as V8NativeObject;
        }

        public static implicit operator HandleProxy*(InternalHandle handle)
        {
            return handle._HandleProxy;
        }

        public static implicit operator InternalHandle(HandleProxy* handleProxy)
        {
            return handleProxy != null ? new InternalHandle(handleProxy) : InternalHandle.Empty;
        }

        // --------------------------------------------------------------------------------------------------------------------

        public static bool operator ==(InternalHandle h1, InternalHandle h2)
        {
            return h1._HandleProxy == h2._HandleProxy;
        }

        public static bool operator !=(InternalHandle h1, InternalHandle h2)
        {
            return !(h1 == h2);
        }

        // --------------------------------------------------------------------------------------------------------------------

        public static implicit operator bool(InternalHandle handle)
        {
            return (bool)Types.ChangeType(handle.Value, typeof(bool));
        }

        public static implicit operator Int32(InternalHandle handle)
        {
            return (Int32)Types.ChangeType(handle.Value, typeof(Int32));
        }

        public static implicit operator double(InternalHandle handle)
        {
            return (double)Types.ChangeType(handle.Value, typeof(double));
        }

        public static implicit operator string(InternalHandle handle)
        {
            return handle.ToString();
        }

        public static implicit operator DateTime(InternalHandle handle)
        {
            var ms = (double)Types.ChangeType(handle.Value, typeof(double));
            return new DateTime(1970, 1, 1) + TimeSpan.FromMilliseconds(ms);
        }

        public static implicit operator JSProperty(InternalHandle handle)
        {
            return new JSProperty(handle);
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// This is used internally to pass on the internal handle values to internally called methods.
        /// This is required because the last method in a call chain is responsible to dispose first-time handles.
        /// (first-time handles are handles created internally immediately after native proxy calls)
        /// </summary>
        public InternalHandle PassOn()
        {
            InternalHandle h = this;
            _First = false; // ("first" is normally only true if the system created handle is not set to another handle)
            return h;
        }

        // --------------------------------------------------------------------------------------------------------------------
        #region ### SHARED HANDLE CODE START ###

        /// <summary>
        /// The ID (index) of this handle on both the native and managed sides.
        /// </summary>
        public int ID { get { return _HandleProxy != null ? _HandleProxy->ID : -1; } }

        /// <summary>
        /// The JavaScript type this handle represents.
        /// </summary>
        public JSValueType ValueType { get { return _HandleProxy != null ? _HandleProxy->_Type : JSValueType.Undefined; } }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// A reference to the V8Engine instance that owns this handle.
        /// </summary>
        public V8Engine Engine { get { return _HandleProxy->EngineID >= 0 ? V8Engine._Engines[_HandleProxy->EngineID] : null; } }

        // --------------------------------------------------------------------------------------------------------------------
        // Managed Object Properties and References

        /// <summary>
        /// The ID of the managed object represented by this handle.
        /// This ID is expected when handles are passed to 'V8ManagedObject.GetObject()'.
        /// <para>If this value is less than 0 then it is a user specified ID tracking value, and as such there is no associated
        /// 'V8NativeObject' object, and the 'Object' property will be null.  This occurs </para>
        /// </summary>
        public Int32 ObjectID
        {
            get
            {
                return _HandleProxy == null ? -1 : _HandleProxy->_ObjectID < -1 || _HandleProxy->_ObjectID >= 0 ? _HandleProxy->_ObjectID : -1;
            }
            internal set { if (_HandleProxy != null) _HandleProxy->_ObjectID = value; }
        }

        /// <summary>
        /// Returns the managed object ID "as is" from the native HandleProxy object.
        /// </summary>
        internal Int32 _CurrentObjectID
        {
            get { return _HandleProxy != null ? _HandleProxy->_ObjectID : -1; }
            set { if (_HandleProxy != null) _HandleProxy->_ObjectID = value; }
        }

        /// <summary>
        /// A reference to the managed object associated with this handle. This property is only valid for object handles, and will return null otherwise.
        /// Upon reading this property, if the managed object has been garbage collected (because no more handles or references exist), then a new basic 'V8NativeObject' instance will be created.
        /// <para>Instead of checking for 'null' (which may not work as expected), query 'HasManagedObject' instead.</para>
        /// </summary>
        public V8NativeObject Object
        {
            get
            {
                if (_HandleProxy != null && (_HandleProxy->_ObjectID >= 0 || _HandleProxy->_ObjectID == -1 && HasObject))
                {
                    var weakRef = Engine._GetObjectWeakReference(_HandleProxy->_ObjectID);
                    return weakRef != null ? weakRef.Reset() : null;
                }
                else
                    return null;
            }
        }

        /// <summary>
        /// If this handle represents an object instance binder, then this returns the bound object.
        /// Bound objects are usually custom user objects (non-V8.NET objects) wrapped in ObjectBinder instances.
        /// </summary>
        public object BoundObject { get { return BindingMode == BindingMode.Instance ? ((ObjectBinder)Object).Object : null; } }

        object IBasicHandle.Object { get { return BoundObject ?? Object; } }

        InternalHandle IHandleBased.InternalHandle { get { return this; } }

        /// <summary>
        /// Returns the registered type ID for objects that represent registered CLR types.
        /// </summary>
        public Int32 CLRTypeID { get { return _HandleProxy != null ? _HandleProxy->_CLRTypeID : -1; } }

        /// <summary>
        /// If this handle represents a type binder, then this returns the associated 'TypeBinder' instance.
        /// <para>Bound types are usually non-V8.NET types that are wrapped and exposed in the JavaScript environment for use with the 'new' operator.</para>
        /// </summary>
        public TypeBinder TypeBinder
        {
            get
            {
                return BindingMode == BindingMode.Static ? ((TypeBinderFunction)Object).TypeBinder
                    : BindingMode == BindingMode.Instance ? ((ObjectBinder)Object).TypeBinder : null;
            }
        }

        /// <summary>
        /// Returns true if this handle is associated with a managed object.
        /// <para>Note: This can be false even though 'IsObjectType' may be true.
        /// A handle can represent a native V8 object handle without requiring an associated managed object.</para>
        /// </summary>
        public bool HasObject
        {
            get
            {
                if (_HandleProxy->_ObjectID >= -1 && IsObjectType && ObjectID >= 0)
                {
                    var weakRef = Engine._GetObjectWeakReference(_CurrentObjectID);
                    return weakRef != null;
                }
                else return false;
            }
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reading from this property returns either the underlying managed object, or else causes a native call to fetch
        /// the current V8 value associated with this handle.
        /// <param>For objects, this returns the in-script type text as a string - unless this handle represents an object binder, in which case this will return the bound object instead.</param>
        /// </summary>
        public object Value
        {
            get
            {
                if (_HandleProxy != null)
                {
                    if (IsBinder)
                        return BoundObject;

                    if (CLRTypeID >= 0)
                    {
                        var argInfo = new ArgInfo(this);
                        return argInfo.ValueOrDefault; // (this object represents a ArgInfo object, so return its value)
                    }

                    V8NetProxy.UpdateHandleValue(_HandleProxy);
                    return _HandleProxy->Value;
                }
                else return null;
            }
        }

        /// <summary>
        /// Reading from this property returns either the underlying managed object, or else causes a ONE-TIME native call to 
        /// fetch the current V8 value associated with this handle. This can be a bit faster, as subsequent calls return the
        /// same value.
        /// <para>Note: If the underlying V8 handle value will change, you should use the 'Value' property instead to make sure
        /// any changes are reflected each time.  Only use this property more than once if you're sure it will not change.</para>
        /// </summary>
        public object LastValue
        {
            get
            {
                if (_HandleProxy != null)
                {
                    if (IsBinder)
                        return BoundObject;

                    if (CLRTypeID >= 0)
                    {
                        var argInfo = new ArgInfo(this);
                        return argInfo.ValueOrDefault; // (this object represents a ArgInfo object, so return its value)
                    }

                    if (_HandleProxy->_Type != JSValueType.Uninitialized)
                        V8NetProxy.UpdateHandleValue(_HandleProxy);
                    return _HandleProxy->Value;
                }
                else return null;
            }
        }

        /// <summary>
        /// Returns the array length for handles that represent arrays. For all other types, this returns 0.
        /// Note: To get the items of the array, use 'GetProperty(#)'.
        /// </summary>
        public Int32 ArrayLength { get { return IsArray ? V8NetProxy.GetArrayLength(_HandleProxy) : 0; } }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns 'true' if this handle is associated with a managed object that has no other CLR based references, and the
        /// GC finalizer has attempted to claim it.
        /// </summary>
        public bool IsWeakManagedObject
        {
            get
            {
                var id = _CurrentObjectID;
                if (id >= 0)
                {
                    var owr = Engine._GetObjectWeakReference(_CurrentObjectID); // (return true when the internal reference becomes null)
                    if (owr != null)
                        return owr.IsGCReady;
                    else
                        _CurrentObjectID = id = -1; // (this ID is no longer valid)
                }
                return id == -1;
            }
        }

        /// <summary>
        /// Returns true if this is the only handle referencing the enclosed handle proxy, and has not yet been disposed.
        /// </summary>
        public bool IsOnlyReference { get { return _HandleProxy != null && _HandleProxy->ManagedReference == 1; } }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// True if this handle is place into a queue to be disposed.
        /// </summary>
        public bool IsBeingDisposed
        {
            get { return _HandleProxy != null && _HandleProxy->IsBeingDisposed; }
            internal set { if (_HandleProxy != null) _HandleProxy->IsBeingDisposed = value; }
        }

        /// <summary>
        /// True if this handle was made weak on the native side (for object handles only).  Once a handle is weak, the V8 garbage collector can collect the
        /// handle (and any associated managed object) at any time.
        /// </summary>
        public bool IsNativelyWeak
        {
            get { return _HandleProxy != null && _HandleProxy->IsWeak; }
        }

        /// <summary>
        /// Returns true if this handle has no references (usually a primitive type), or is the only reference AND is associated with a weak managed object reference.
        /// When a handle is ready to be disposed, then calling "Dispose()" will succeed and cause the handle to be placed back into the cache on the native side.
        /// </summary>
        public bool IsDisposeReady { get { return _HandleProxy != null && (_HandleProxy->ManagedReference == 0 || IsOnlyReference && IsWeakManagedObject); } }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Forces the underlying object, if any, to separate from the handle.  This is done by swapping the object with a
        /// place holder object to keep the ID (index) for the current object alive until the native V8 engine's GC can remove
        /// any associated handles later.  The released object is returned, or null if there is no object.
        /// </summary>
        /// <returns>The object released.</returns>
        public V8NativeObject ReleaseManagedObject()
        {
            if (IsObjectType && ObjectID >= 0)
                using (Engine._ObjectsLocker.ReadLock())
                {
                    var weakRef = Engine._GetObjectWeakReference(ObjectID);
                    if (weakRef != null)
                    {
                        var obj = weakRef.Object;
                        var placeHolder = new V8NativeObject();
                        placeHolder._Engine = obj._Engine;
                        placeHolder.Template = obj.Template;
                        weakRef.SetTarget(placeHolder); // (this must be done first before moving the handle to the new object!)
                        placeHolder._Handle = obj._Handle;
                        obj.Template = null;
                        obj._ID = null;
                        obj._Handle.Set(InternalHandle.Empty);
                        return obj;
                    }
                }
            return null;
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns true if this handle is empty (that is, equal to 'Handle.Empty'), and false if a valid handle exists.
        /// <para>An empty state is when a handle is set to 'InternalHandle.Empty' and has no valid native V8 handle assigned.
        /// This is similar to "undefined"; however, this property will be true if a valid native V8 handle exists that is set to "undefined".</para>
        /// </summary>
        public bool IsEmpty { get { return _HandleProxy == null; } }

        /// <summary>
        /// Returns true if this handle is undefined or empty (empty is when this handle is an instance of 'Handle.Empty').
        /// <para>"Undefined" does not mean "null".  A variable (handle) can be defined and set to "null".</para>
        /// </summary>
        public bool IsUndefined { get { return IsEmpty || ValueType == JSValueType.Undefined; } }

        /// <summary>
        /// Returns 'true' if this handle represents a ''ull' value (that is, an explicitly defined 'null' value).
        /// This will return 'false' if 'IsEmpty' or 'IsUndefined' is true.
        /// </summary>
        public bool IsNull { get { return ValueType == JSValueType.Null; } }

        public bool IsBoolean { get { return ValueType == JSValueType.Bool; } }
        public bool IsBooleanObject { get { return ValueType == JSValueType.BoolObject; } }
        public bool IsInt32 { get { return ValueType == JSValueType.Int32; } }
        public bool IsNumber { get { return ValueType == JSValueType.Number; } }
        public bool IsNumberObject { get { return ValueType == JSValueType.NumberObject; } }
        public bool IsString { get { return ValueType == JSValueType.String; } }
        public bool IsStringObject { get { return ValueType == JSValueType.StringObject; } }
        public bool IsObject { get { return ValueType == JSValueType.Object; } }
        public bool IsFunction { get { return ValueType == JSValueType.Function; } }
        public bool IsDate { get { return ValueType == JSValueType.Date; } }
        public bool IsArray { get { return ValueType == JSValueType.Array; } }
        public bool IsRegExp { get { return ValueType == JSValueType.RegExp; } }

        /// <summary>
        /// Returns true of the handle represents ANY object type.
        /// </summary>
        public bool IsObjectType
        {
            get
            {
                return ValueType == JSValueType.BoolObject
                    || ValueType == JSValueType.NumberObject
                    || ValueType == JSValueType.StringObject
                    || ValueType == JSValueType.Function
                    || ValueType == JSValueType.Date
                    || ValueType == JSValueType.RegExp
                    || ValueType == JSValueType.Array
                    || ValueType == JSValueType.Object;
            }
        }

        /// <summary>
        /// Used internally to quickly determine when an instance represents a binder object type (faster than reflection!).
        /// </summary>
        public bool IsBinder { get { return IsObjectType && HasObject && Object._BindingMode != BindingMode.None; } }

        /// <summary>
        /// Returns the binding mode (Instance, Static, or None) represented by this handle.  The return is 'None' (0) if not applicable.
        /// </summary>
        public BindingMode BindingMode { get { return IsBinder ? Object._BindingMode : BindingMode.None; } }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns the 'Value' property type cast to the expected type.
        /// Warning: No conversion is made between different value types.
        /// </summary>
        public DerivedType As<DerivedType>()
        {
            return _HandleProxy != null ? (DerivedType)Value : default(DerivedType);
        }

        /// Returns the 'LastValue' property type cast to the expected type.
        /// Warning: No conversion is made between different value types.
        public DerivedType LastAs<DerivedType>()
        {
            return _HandleProxy != null ? (DerivedType)LastValue : default(DerivedType);
        }

        /// <summary>
        /// Returns the underlying value converted if necessary to a Boolean type.
        /// </summary>
        public bool AsBoolean { get { return (bool)this; } }

        /// <summary>
        /// Returns the underlying value converted if necessary to an Int32 type.
        /// </summary>
        public Int32 AsInt32 { get { return (Int32)this; } }

        /// <summary>
        /// Returns the underlying value converted if necessary to a double type.
        /// </summary>
        public double AsDouble { get { return (double)this; } }

        /// <summary>
        /// Returns the underlying value converted if necessary to a string type.
        /// </summary>
        public String AsString { get { return (String)this; } }

        /// <summary>
        /// Returns the underlying value converted if necessary to a DateTime type.
        /// </summary>
        public DateTime AsDate { get { return (DateTime)this; } }

        /// <summary>
        /// Returns this handle as a new JSProperty instance with default property attributes.
        /// </summary>
        public IJSProperty AsJSProperty() { return (JSProperty)this; }

        // --------------------------------------------------------------------------------------------------------------------

        public string DisposalStatus
        {
            get
            {
                switch (_HandleProxy->Disposed)
                {
                    case 0: break;
                    case 1: return "Ready For Disposal";
                    case 2: return "Weak (waiting on V8 GC)";
                    case 3: return "Disposed And cached";
                }
                return "";
            }
        }

        internal string _IDAndRefStatus { get { return "Engine ID: " + _EngineID + ", Handle ID: " + _HandleProxy->ID + ", References: " + _HandleProxy->ManagedReference; } }

        /// <summary>
        /// Returns a string describing the handle (mainly for debugging purposes).
        /// </summary>
        public string Description
        {
            get
            {
                try
                {
                    if (IsEmpty) return "<empty>";
                    if (_EngineID < 0 || Engine == null) return "<detached>"; // (doesn't belong anywhere - gone rogue! ;) )
                    if (IsDisposed) return "<" + _IDAndRefStatus + ", Object ID: " + _HandleProxy->_ObjectID + ",  " + DisposalStatus + ">";
                    if (IsUndefined) return "undefined";

                    if (IsBinder)
                    {
                        if (BindingMode == BindingMode.Static)
                        {
                            var typeBinder = ((TypeBinderFunction)Object).TypeBinder;
                            return "<CLR Type: " + typeBinder.BoundType.FullName + ", " + _IDAndRefStatus + ">";
                        }
                        else
                        {
                            var obj = BoundObject;
                            if (obj != null)
                                return "<" + obj.ToString() + ", " + _IDAndRefStatus + ">";
                            else
                                throw new InvalidOperationException("Object binder does not have an object instance.");
                        }
                    }
                    else if (IsObjectType)
                    {
                        string managedType = "";
                        string disposal = !string.IsNullOrEmpty(DisposalStatus) ? " - " + DisposalStatus : "";

                        if (HasObject)
                        {
                            var mo = Engine._GetObjectAsIs(ObjectID);
                            managedType = " (" + (mo != null ? mo.GetType().Name + " [" + mo.ID + "]" : "associated managed object is null") + ")";
                        }

                        var typeName = Engine.GlobalObject != this ? Enum.GetName(typeof(JSValueType), ValueType) : "global";

                        return "<object: " + typeName + managedType + disposal + ", " + _IDAndRefStatus + ">";
                    }
                    else
                    {
                        // ... this is a value type ...
                        var val = Value;
                        return "<value: " + (string)Types.ChangeType(val, typeof(string)) + ", " + _IDAndRefStatus + ">";
                    }
                }
                catch (Exception ex)
                {
                    return Exceptions.GetFullErrorMessage(ex);
                }
            }
        }

        /// <summary>
        /// If this handle represents an object, the 'Description' property value is returned, otherwise the primitive value
        /// is returned as a string.
        /// <para>Note: This does not pull the same string as calling 'toString()' in JavaScript.  You have to use the 'Call()' 
        /// method on this handle to call that function - as you would normally.</para>
        /// </summary>
        public override string ToString()
        {
            try
            {
                if (IsEmpty) return "<empty>";
                if (_EngineID < 0 || Engine == null) return "<detached>"; // (doesn't belong anywhere - gone rogue! ;) )
                if (IsDisposed) return "<" + _IDAndRefStatus + ", Object ID: " + _HandleProxy->_ObjectID + ",  " + DisposalStatus + ">";
                if (IsUndefined) return "undefined";
                if (IsObjectType) return Description;
                var val = Value;
                return val != null ? (string)Types.ChangeType(val, typeof(string)) : "null";
            }
            catch (Exception ex)
            {
                return Exceptions.GetFullErrorMessage(ex);
            }
        }

        /// <summary>
        /// Checks if the wrapped handle reference is the same as the one compared with. This DOES NOT compare the underlying JavaScript values for equality.
        /// To test for JavaScript value equality, convert to a desired value-type instead by first casting as needed (i.e. (int)jsv1 == (int)jsv2).
        /// </summary>
        public override bool Equals(object obj)
        {
            return obj is IHandleBased && _HandleProxy == ((IHandleBased)obj).InternalHandle._HandleProxy;
        }

        public override int GetHashCode()
        {
            return (int)ID;
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns true if this handle contains an error message (the string value is the message).
        /// If you have exception catching in place, you can simply call 'ThrowOnError()' instead.
        /// </summary>
        public bool IsError
        {
            get
            {
                return ValueType == JSValueType.InternalError
                    || ValueType == JSValueType.CompilerError
                    || ValueType == JSValueType.ExecutionError;
            }
        }

        /// <summary>
        /// Checks if the handle represents an error, and if so, throws one of the corresponding derived V8Exception exceptions.
        /// See 'JSValueType' for possible exception states.  You can check the 'IsError' property to see if this handle represents an error.
        /// <para>Exceptions thrown: V8InternalErrorException, V8CompilerErrorException, V8ExecutionErrorException, and V8Exception (for any general V8-related exceptions).</para>
        /// </summary>
        public void ThrowOnError()
        {
            if (IsError)
                switch (ValueType)
                {
                    case JSValueType.InternalError: throw new V8InternalErrorException(this);
                    case JSValueType.CompilerError: throw new V8CompilerErrorException(this);
                    case JSValueType.ExecutionError: throw new V8ExecutionErrorException(this);
                    default: throw new V8Exception(this); // (this will only happen if 'IsError' contains a type check that doesn't have any corresponding exception object)
                }
        }

        // --------------------------------------------------------------------------------------------------------------------
        // DynamicObject support is in .NET 4.0 and higher

#if !(V1_1 || V2 || V3 || V3_5)
        #region IDynamicMetaObjectProvider Members

        public DynamicMetaObject GetMetaObject(Expression parameter)
        {
            return new DynamicHandle(this, parameter);
        }

        #endregion
#endif

        // --------------------------------------------------------------------------------------------------------------------
        // IConvertable

        public TypeCode GetTypeCode()
        {
            switch (ValueType)
            {
                case JSValueType.Array:
                case JSValueType.Date:
                case JSValueType.Function:
                case JSValueType.Object:
                case JSValueType.RegExp:
                    return TypeCode.Object;
                case JSValueType.Bool:
                case JSValueType.BoolObject:
                    return TypeCode.Boolean;
                case JSValueType.Int32:
                    return TypeCode.Int32;
                case JSValueType.Number:
                case JSValueType.NumberObject:
                    return TypeCode.Double;
                case JSValueType.String:
                case JSValueType.CompilerError:
                case JSValueType.ExecutionError:
                case JSValueType.InternalError:
                    return TypeCode.String;
            }
            return TypeCode.Empty;
        }

        public bool ToBoolean(IFormatProvider provider) { return Types.ChangeType<bool>(Value, provider); }
        public byte ToByte(IFormatProvider provider) { return Types.ChangeType<byte>(Value, provider); }
        public char ToChar(IFormatProvider provider) { return Types.ChangeType<char>(Value, provider); }
        public DateTime ToDateTime(IFormatProvider provider) { return Types.ChangeType<DateTime>(Value, provider); }
        public decimal ToDecimal(IFormatProvider provider) { return Types.ChangeType<decimal>(Value, provider); }
        public double ToDouble(IFormatProvider provider) { return Types.ChangeType<double>(Value, provider); }
        public short ToInt16(IFormatProvider provider) { return Types.ChangeType<Int16>(Value, provider); }
        public int ToInt32(IFormatProvider provider) { return Types.ChangeType<Int32>(Value, provider); }
        public long ToInt64(IFormatProvider provider) { return Types.ChangeType<Int64>(Value, provider); }
        public sbyte ToSByte(IFormatProvider provider) { return Types.ChangeType<sbyte>(Value, provider); }
        public float ToSingle(IFormatProvider provider) { return Types.ChangeType<Single>(Value, provider); }
        public string ToString(IFormatProvider provider) { return Types.ChangeType<string>(Value, provider); }
        public object ToType(Type conversionType, IFormatProvider provider) { return Types.ChangeType(Value, conversionType, provider); }
        public ushort ToUInt16(IFormatProvider provider) { return Types.ChangeType<UInt16>(Value, provider); }
        public uint ToUInt32(IFormatProvider provider) { return Types.ChangeType<UInt32>(Value, provider); }
        public ulong ToUInt64(IFormatProvider provider) { return Types.ChangeType<UInt64>(Value, provider); }

        #endregion ### SHARED HANDLE CODE END ###
        // --------------------------------------------------------------------------------------------------------------------

        internal const string _NOT_AN_OBJECT_ERRORMSG = "The handle does not represent a JavaScript object.";
        internal const string _VALUE_NOT_AN_OBJECT_ERRORMSG = "The handle {0} does not represent a JavaScript object.";

        /// <summary>
        /// Calls the V8 'Set()' function on the underlying native object.
        /// Returns true if successful.
        /// </summary>
        /// <param name="attributes">Flags that describe the property behavior.  They must be 'OR'd together as needed.</param>
        public bool SetProperty(string name, InternalHandle value, V8PropertyAttributes attributes = V8PropertyAttributes.None)
        {
            try
            {
                if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException("name (cannot be null, empty, or only whitespace)");

                if (!IsObjectType)
                    throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

                return V8NetProxy.SetObjectPropertyByName(this, name, value, attributes);
            }
            finally
            {
                value._DisposeIfFirst();
            }
        }

        /// <summary>
        /// Calls the V8 'Set()' function on the underlying native object.
        /// Returns true if successful.
        /// </summary>
        public bool SetProperty(Int32 index, InternalHandle value)
        {
            try
            {
                // ... can only set properties on objects ...

                if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

                return V8NetProxy.SetObjectPropertyByIndex(this, index, value);
            }
            finally
            {
                value._DisposeIfFirst();
            }
        }

        /// <summary>
        /// Sets a property to a given object. If the object is not V8.NET related, then the system will attempt to bind the instance and all public members to
        /// the specified property name.
        /// Returns true if successful.
        /// </summary>
        /// <param name="name">The property name. If 'null', then the name of the object type is assumed.</param>
        /// <param name="obj">Some value or object instance. 'Engine.CreateValue()' will be used to convert value types, unless the object is already a handle, in which case it is set directly.</param>
        /// <param name="className">A custom in-script function name for the specified object type, or 'null' to use either the type name as is (the default) or any existing 'ScriptObject' attribute name.</param>
        /// <param name="recursive">For object instances, if true, then object reference members are included, otherwise only the object itself is bound and returned.
        /// For security reasons, public members that point to object instances will be ignored. This must be true to included those as well, effectively allowing
        /// in-script traversal of the object reference tree (so make sure this doesn't expose sensitive methods/properties/fields).</param>
        /// <param name="memberSecurity">For object instances, these are default flags that describe JavaScript properties for all object instance members that
        /// don't have any 'ScriptMember' attribute.  The flags should be 'OR'd together as needed.</param>
        public bool SetProperty(string name, object obj, string className = null, bool? recursive = null, ScriptMemberSecurity? memberSecurity = null)
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            if (name.IsNullOrWhiteSpace())
                if (obj == null) throw new InvalidOperationException("You cannot pass 'null' without a valid property name.");
                else
                    name = obj.GetType().Name;

            if (obj is IHandleBased)
                return SetProperty(name, ((IHandleBased)obj).InternalHandle, (V8PropertyAttributes)(memberSecurity ?? ScriptMemberSecurity.ReadWrite));

            if (obj == null || obj is string || obj.GetType().IsValueType) // TODO: Check enum support.
                return SetProperty(name, Engine.CreateValue(obj), (V8PropertyAttributes)(memberSecurity ?? ScriptMemberSecurity.ReadWrite));

            var nObj = Engine.CreateBinding(obj, className, recursive, memberSecurity);

            if (memberSecurity != null)
                return SetProperty(name, nObj, (V8PropertyAttributes)memberSecurity);
            else
                return SetProperty(name, nObj);
        }

        /// <summary>
        /// Binds a 'V8Function' object to the specified type and associates the type name (or custom script name) with the underlying object.
        /// Returns true if successful.
        /// </summary>
        /// <param name="type">The type to wrap.</param>
        /// <param name="propertyAttributes">Flags that describe the property behavior.  They must be 'OR'd together as needed.</param>
        /// <param name="className">A custom in-script function name for the specified type, or 'null' to use either the type name as is (the default) or any existing 'ScriptObject' attribute name.</param>
        /// <param name="recursive">For object types, if true, then object reference members are included, otherwise only the object itself is bound and returned.
        /// For security reasons, public members that point to object instances will be ignored. This must be true to included those as well, effectively allowing
        /// in-script traversal of the object reference tree (so make sure this doesn't expose sensitive methods/properties/fields).</param>
        /// <param name="memberSecurity">For object instances, these are default flags that describe JavaScript properties for all object instance members that
        /// don't have any 'ScriptMember' attribute.  The flags should be 'OR'd together as needed.</param>
        public bool SetProperty(Type type, V8PropertyAttributes propertyAttributes = V8PropertyAttributes.None, string className = null, bool? recursive = null, ScriptMemberSecurity? memberSecurity = null)
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            var func = (V8Function)Engine.CreateBinding(type, className, recursive, memberSecurity).Object;

            return SetProperty(func.FunctionTemplate.ClassName, func, propertyAttributes);
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Calls the V8 'Get()' function on the underlying native object.
        /// If the property doesn't exist, the 'IsUndefined' property will be true.
        /// </summary>
        public InternalHandle GetProperty(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException("name (cannot be null, empty, or only whitespace)");

            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return V8NetProxy.GetObjectPropertyByName(this, name);
        }

        /// <summary>
        /// Calls the V8 'Get()' function on the underlying native object.
        /// If the property doesn't exist, the 'IsUndefined' property will be true.
        /// </summary>
        public InternalHandle GetProperty(Int32 index)
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return V8NetProxy.GetObjectPropertyByIndex(this, index);
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Calls the V8 'Delete()' function on the underlying native object.
        /// Returns true if the property was deleted.
        /// </summary>
        public bool DeleteProperty(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException("name (cannot be null, empty, or only whitespace)");

            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return V8NetProxy.DeleteObjectPropertyByName(this, name);
        }

        /// <summary>
        /// Calls the V8 'Delete()' function on the underlying native object.
        /// Returns true if the property was deleted.
        /// </summary>
        public bool DeleteProperty(Int32 index)
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return V8NetProxy.DeleteObjectPropertyByIndex(this, index);
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Calls the V8 'SetAccessor()' function on the underlying native object to create a property that is controlled by "getter" and "setter" callbacks.
        /// </summary>
        public void SetAccessor(string name,
            V8NativeObjectPropertyGetter getter, V8NativeObjectPropertySetter setter,
            V8PropertyAttributes attributes = V8PropertyAttributes.None, V8AccessControl access = V8AccessControl.Default)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException("name (cannot be null, empty, or only whitespace)");
            if (attributes == V8PropertyAttributes.Undefined)
                attributes = V8PropertyAttributes.None;
            if (attributes < 0) throw new InvalidOperationException("'attributes' has an invalid value.");
            if (access < 0) throw new InvalidOperationException("'access' has an invalid value.");

            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            var engine = Engine;

            // TODO: Need a different native ID to track this.
            V8NetProxy.SetObjectAccessor(this, ObjectID, name,
                   Engine._StoreAccessor<ManagedAccessorGetter>(ObjectID, "get_" + name, (HandleProxy* _this, string propertyName) =>
                   {
                       try
                       {
                           using (InternalHandle hThis = _this) { return getter != null ? getter(hThis, propertyName).Clone() : null; }
                       }
                       catch (Exception ex)
                       {
                           return engine.CreateError(Exceptions.GetFullErrorMessage(ex), JSValueType.ExecutionError);
                       }
                   }),
                   Engine._StoreAccessor<ManagedAccessorSetter>(ObjectID, "set_" + name, (HandleProxy* _this, string propertyName, HandleProxy* value) =>
                   {
                       try
                       {
                           using (InternalHandle hThis = _this) { return setter != null ? setter(hThis, propertyName, value).Clone() : null; }
                       }
                       catch (Exception ex)
                       {
                           return engine.CreateError(Exceptions.GetFullErrorMessage(ex), JSValueType.ExecutionError);
                       }
                   }),
                   access, attributes);
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Returns a list of all property names for this object (including all objects in the prototype chain).
        /// </summary>
        public string[] GetPropertyNames()
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            using (InternalHandle v8array = V8NetProxy.GetPropertyNames(this))
            {
                var length = V8NetProxy.GetArrayLength(v8array);

                var names = new string[length];

                InternalHandle itemHandle;

                for (var i = 0; i < length; i++)
                    using (itemHandle = V8NetProxy.GetObjectPropertyByIndex(v8array, i))
                    {
                        names[i] = itemHandle;
                    }

                return names;
            }
        }

        /// <summary>
        /// Returns a list of all property names for this object (excluding the prototype chain).
        /// </summary>
        public string[] GetOwnPropertyNames()
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            using (InternalHandle v8array = V8NetProxy.GetOwnPropertyNames(this))
            {
                var length = V8NetProxy.GetArrayLength(v8array);

                var names = new string[length];

                InternalHandle itemHandle;

                for (var i = 0; i < length; i++)
                    using (itemHandle = V8NetProxy.GetObjectPropertyByIndex(v8array, i))
                    {
                        names[i] = itemHandle;
                    }

                return names;
            }
        }

        // --------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Get the attribute flags for a property of this object.
        /// If a property doesn't exist, then 'V8PropertyAttributes.None' is returned
        /// (Note: only V8 returns 'None'. The value 'Undefined' has an internal proxy meaning for property interception).</para>
        /// </summary>
        public V8PropertyAttributes GetPropertyAttributes(string name)
        {
            if (name.IsNullOrWhiteSpace()) throw new ArgumentNullException("name (cannot be null, empty, or only whitespace)");

            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return V8NetProxy.GetPropertyAttributes(this, name);
        }

        // --------------------------------------------------------------------------------------------------------------------

        internal InternalHandle _Call(string functionName, InternalHandle _this, params InternalHandle[] args)
        {
            try
            {
                if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

                HandleProxy** nativeArrayMem = Utilities.MakeHandleProxyArray(args);

                var result = V8NetProxy.Call(this, functionName, _this, args.Length, nativeArrayMem);

                Utilities.FreeNativeMemory((IntPtr)nativeArrayMem);

                return result;
            }
            finally
            {
                _this._DisposeIfFirst();
                for (var i = 0; i < args.Length; i++)
                    args[i]._DisposeIfFirst();
            }
        }

        /// <summary>
        /// Calls the specified function property on the underlying object.
        /// The '_this' parameter is the "this" reference within the function when called.
        /// </summary>
        public InternalHandle Call(string functionName, InternalHandle _this, params InternalHandle[] args)
        {
            if (functionName.IsNullOrWhiteSpace()) throw new ArgumentNullException("functionName (cannot be null, empty, or only whitespace)");

            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return _Call(functionName, _this, args);
        }

        /// <summary>
        /// Calls the specified function property on the underlying object.
        /// The 'this' property will not be specified, which will default to the global scope as expected.
        /// </summary>
        public InternalHandle StaticCall(string functionName, params InternalHandle[] args)
        {
            if (functionName.IsNullOrWhiteSpace()) throw new ArgumentNullException("functionName (cannot be null, empty, or only whitespace)");

            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return _Call(functionName, InternalHandle.Empty, args);
        }

        /// <summary>
        /// Calls the underlying object as a function.
        /// The '_this' parameter is the "this" reference within the function when called.
        /// </summary>
        public InternalHandle Call(InternalHandle _this, params InternalHandle[] args)
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return _Call(null, _this, args);
        }

        /// <summary>
        /// Calls the underlying object as a function.
        /// The 'this' property will not be specified, which will default to the global scope as expected.
        /// </summary>
        public InternalHandle StaticCall(params InternalHandle[] args)
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);

            return _Call(null, InternalHandle.Empty, args);
        }

        // --------------------------------------------------------------------------------------------------------------------

        [Obsolete("Using object inspector on this handle will cause handle leaks when this property is read from.  Please use 'GetPrototype()' instead.", true)]
        public InternalHandle Prototype // (cannot be a property, else the object inspector will cause handle leaks)
        { get { throw new NotSupportedException(); } }

        /// <summary>
        /// The prototype of the object (every JavaScript object implicitly has a prototype).
        /// <para>Note: As with any InternalHandle returned, you are responsible to dispose it.  It is recommended to type cast
        /// this to an ObjectHandle before use.</para>
        /// </summary>
        public InternalHandle GetPrototype() // (cannot be a property, else )
        {
            if (!IsObjectType) throw new InvalidOperationException(_NOT_AN_OBJECT_ERRORMSG);
            return V8NetProxy.GetObjectPrototype(_HandleProxy);
        }

        // --------------------------------------------------------------------------------------------------------------------
    }

    // ========================================================================================================================

    /// <summary>
    /// Intercepts JavaScript access for properties on the associated JavaScript object for retrieving a value.
    /// <para>To allow the V8 engine to perform the default get action, return "Handle.Empty".</para>
    /// </summary>
    public delegate InternalHandle V8NativeObjectPropertyGetter(InternalHandle _this, string propertyName);

    /// <summary>
    /// Intercepts JavaScript access for properties on the associated JavaScript object for setting values.
    /// <para>To allow the V8 engine to perform the default set action, return "Handle.Empty".</para>
    /// </summary>
    public delegate InternalHandle V8NativeObjectPropertySetter(InternalHandle _this, string propertyName, InternalHandle value);

    // ========================================================================================================================

    public unsafe partial class V8Engine
    {
        internal readonly Dictionary<Int32, Dictionary<string, Delegate>> _Accessors = new Dictionary<Int32, Dictionary<string, Delegate>>();

        /// <summary>
        /// This is required in order prevent accessor delegates from getting garbage collected when used with P/Invoke related callbacks (a process called "thunking").
        /// </summary>
        /// <typeparam name="T">The type of delegate ('d') to store and return.</typeparam>
        /// <param name="key">A native pointer (usually a proxy object) to associated the delegate to.</param>
        /// <param name="d">The delegate to keep a strong reference to (expected to be of type 'T').</param>
        /// <returns>The same delegate passed in, cast to type of 'T'.</returns>
        internal T _StoreAccessor<T>(Int32 id, string propertyName, T d) where T : class
        {
            Dictionary<string, Delegate> delegates;
            if (!_Accessors.TryGetValue(id, out delegates))
                _Accessors[id] = delegates = new Dictionary<string, Delegate>();
            delegates[propertyName] = (Delegate)(object)d;
            return d;
        }

        /// <summary>
        /// Returns true if there are any delegates associated with the given object reference.
        /// </summary>
        internal bool _HasAccessors(Int32 id)
        {
            Dictionary<string, Delegate> delegates;
            return _Accessors.TryGetValue(id, out delegates) && delegates.Count > 0;
        }

        /// <summary>
        /// Clears any accessor delegates associated with the given object reference.
        /// </summary>
        internal void _ClearAccessors(Int32 id)
        {
            Dictionary<string, Delegate> delegates;
            if (_Accessors.TryGetValue(id, out delegates))
                delegates.Clear();
        }
    }

    // ========================================================================================================================
}

