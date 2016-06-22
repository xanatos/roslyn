//
// Sample of async methods with Future<T> and Future returning methods.
//

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Futures;

class Program
{
    static void Main()
    {
        F().OnCompleted((f, _) => { Console.WriteLine(f.Result); }, default(object));
        Console.ReadLine();
    }

    static async Future<int> F()
    {
        await G();

        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(100);
            Console.WriteLine(i);
        }

        return 42;
    }

    static async Future G()
    {
        Console.Write("Starting... ");
        await Task.Delay(100);
        Console.WriteLine("Done!");
    }
}

namespace System.Futures
{
    public struct Future<T> : INotifyCompletion
    {
        private readonly FutureCompletionSourceBase<T> _source;

        internal Future(FutureCompletionSourceBase<T> source)
        {
            _source = source;
        }

        public Future<T> GetAwaiter() => this;

        public bool IsCompleted => _source.IsCompleted;
        public bool IsFaulted => _source.IsFaulted;
        public T Result => _source.Result;
        public Exception Error => _source.Error;

        public T GetResult() => _source.Result;

        public void OnCompleted(Action continuation) => _source.OnCompleted(continuation);
        public void OnCompleted<R>(Action<Future<T>, R> continuation, R state) => _source.OnCompleted(continuation, state);

        public Task<T> ToTask()
        {
            if (IsCompleted && !IsFaulted)
            {
                return Task.FromResult(Result);
            }

            var tcs = new TaskCompletionSource<T>();

            OnCompleted((f, c) =>
            {
                if (f.IsFaulted)
                {
                    c.SetException(f.Error);
                }
                else
                {
                    c.SetResult(f.Result);
                }
            }, tcs);

            return tcs.Task;
        }
    }
}

namespace System.Futures
{
    public struct Future : INotifyCompletion
    {
        private static readonly FutureCompletionSourceBase<object> s_completed = new FutureCompletionSourceResult<object>(null);

        private readonly FutureCompletionSourceBase<object> _source;

        internal Future(FutureCompletionSourceBase<object> source)
        {
            _source = source;
        }

        public Future GetAwaiter() => this;

        public bool IsCompleted => _source.IsCompleted;
        public bool IsFaulted => _source.IsFaulted;
        public Exception Error => _source.Error;

        public void GetResult()
        {
            var ignored = _source.Result;
        }

        public void OnCompleted(Action continuation) => _source.OnCompleted(continuation);
        public void OnCompleted<R>(Action<Future, R> continuation, R state) => _source.OnCompleted(continuation, state);

        public Task ToTask()
        {
            if (IsCompleted && !IsFaulted)
            {
                return Task.FromResult(0);
            }

            var tcs = new TaskCompletionSource<object>();

            OnCompleted((f, c) =>
            {
                if (f.IsFaulted)
                {
                    c.SetException(f.Error);
                }
                else
                {
                    c.SetResult(null);
                }
            }, tcs);

            return tcs.Task;
        }

        public static Future Completed => new Future(s_completed);

        public static Future<T> FromResult<T>(T result) => new Future<T>(new FutureCompletionSourceResult<T>(result));
    }
}

namespace System.Futures
{
    public sealed class FutureCompletionSource<T> : FutureCompletionSourceBase<T>
    {
        private const int RUNNING = 0;
        private const int PENDING = 1;
        private const int DONE = 2;
        private const int FAULTED = 3;

        private int _state;
        private T _result;
        private Exception _error;
        private object _continuation;

        public override void SetResult(T result)
        {
            if (!TrySetResult(result))
            {
                throw new InvalidOperationException();
            }
        }

        public override void SetException(Exception error)
        {
            if (error == null)
            {
                throw new ArgumentNullException("error");
            }

            if (!TrySetException(error))
            {
                throw new InvalidOperationException();
            }
        }

        public bool TrySetResult(T result)
        {
            var old = default(int);
            var continuation = default(object);

            try
            {
                while (true)
                {
                    old = Interlocked.CompareExchange(ref _state, PENDING, RUNNING);

                    if (old == PENDING)
                    {
                        new SpinWait().SpinOnce();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (old == RUNNING)
                {
                    continuation = Interlocked.Exchange(ref _continuation, null);

                    _result = result;
                    Volatile.Write(ref _state, DONE);
                }
            }

            if (old == RUNNING)
            {
                if (continuation != null)
                {
                    RunContinuation(continuation);
                }

                return true;
            }

            return false;
        }

        public bool TrySetException(Exception error)
        {
            var old = default(int);
            var continuation = default(object);

            try
            {
                while (true)
                {
                    old = Interlocked.CompareExchange(ref _state, PENDING, RUNNING);

                    if (old == PENDING)
                    {
                        new SpinWait().SpinOnce();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (old == RUNNING)
                {
                    continuation = Interlocked.Exchange(ref _continuation, null);

                    _error = error;
                    Volatile.Write(ref _state, FAULTED);
                }
            }

            if (old == RUNNING)
            {
                if (continuation != null)
                {
                    RunContinuation(continuation);
                }

                return true;
            }

            return false;
        }

        public Future<T> Future
        {
            get
            {
                return new Future<T>(this);
            }
        }

        internal override T Result
        {
            get
            {
                var state = Volatile.Read(ref _state);

                if (state == DONE)
                {
                    return _result;
                }
                else if (state == FAULTED)
                {
                    throw new InvalidOperationException();
                }

                throw new InvalidOperationException();
            }
        }

        internal override Exception Error
        {
            get
            {
                var state = Volatile.Read(ref _state);

                if (state == FAULTED)
                {
                    return _error;
                }
                else if (state == DONE)
                {
                    throw new InvalidOperationException();
                }

                throw new InvalidOperationException();
            }
        }

        internal override bool IsCompleted
        {
            get
            {
                return Volatile.Read(ref _state) >= DONE;
            }
        }

        internal override bool IsFaulted
        {
            get
            {
                return Volatile.Read(ref _state) == FAULTED;
            }
        }

        internal override void OnCompleted<R>(Action<Future<T>, R> continuation, R state)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException("continuation");
            }

            var old = default(int);
            var added = default(bool);

            try
            {
                while (true)
                {
                    old = Interlocked.CompareExchange(ref _state, PENDING, RUNNING);

                    if (old == PENDING)
                    {
                        new SpinWait().SpinOnce();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (old == RUNNING)
                {
                    added = TrySetContinuation(GetContinuation(continuation, state));
                    Volatile.Write(ref _state, RUNNING);
                }
            }

            if (old >= DONE)
            {
                continuation(Future, state);
            }
            else if (old == RUNNING)
            {
                if (!added)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        internal override void OnCompleted<R>(Action<Future, R> continuation, R state)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException("continuation");
            }

            var old = default(int);
            var added = default(bool);

            try
            {
                while (true)
                {
                    old = Interlocked.CompareExchange(ref _state, PENDING, RUNNING);

                    if (old == PENDING)
                    {
                        new SpinWait().SpinOnce();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (old == RUNNING)
                {
                    added = TrySetContinuation(GetContinuation(continuation, state));
                    Volatile.Write(ref _state, RUNNING);
                }
            }

            if (old >= DONE)
            {
                continuation(new Future((FutureCompletionSource<object>)(object)this), state);
            }
            else if (old == RUNNING)
            {
                if (!added)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        internal override void OnCompleted(Action continuation)
        {
            if (continuation == null)
            {
                throw new ArgumentNullException("continuation");
            }

            var old = default(int);
            var added = default(bool);

            try
            {
                while (true)
                {
                    old = Interlocked.CompareExchange(ref _state, PENDING, RUNNING);

                    if (old == PENDING)
                    {
                        new SpinWait().SpinOnce();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (old == RUNNING)
                {
                    added = TrySetContinuation(continuation);
                    Volatile.Write(ref _state, RUNNING);
                }
            }

            if (old >= DONE)
            {
                continuation();
            }
            else if (old == RUNNING)
            {
                if (!added)
                {
                    throw new InvalidOperationException();
                }
            }
        }

        private bool TrySetContinuation(object continuation)
        {
            var old = Volatile.Read(ref _continuation);

            if (old != null)
            {
                return false;
            }

            _continuation = continuation;
            return true;
        }

        private void RunContinuation(object continuation)
        {
            var a = continuation as Action;
            if (a != null)
            {
                a();
            }
            else
            {
                var b = continuation as Action<Future>;
                if (b != null)
                {
                    b(new Future((FutureCompletionSource<object>)(object)this));
                }
                else
                {
                    ((Action<Future<T>>)continuation)(Future);
                }
            }
        }

        private Action<Future> GetContinuation<R>(Action<Future, R> continuation, R state)
        {
            return f => continuation(f, state);
        }

        private Action<Future<T>> GetContinuation<R>(Action<Future<T>, R> continuation, R state)
        {
            return f => continuation(f, state);
        }
    }
}

namespace System.Futures
{
    public abstract class FutureCompletionSourceBase<T>
    {
        internal abstract T Result { get; }

        internal abstract Exception Error { get; }

        internal abstract bool IsCompleted { get; }

        internal abstract bool IsFaulted { get; }

        internal abstract void OnCompleted<R>(Action<Future<T>, R> continuation, R state);

        internal abstract void OnCompleted<R>(Action<Future, R> continuation, R state);

        internal abstract void OnCompleted(Action continuation);

        public abstract void SetException(Exception error);

        public abstract void SetResult(T result);
    }
}

namespace System.Futures
{
    internal class FutureCompletionSourceResult<T> : FutureCompletionSourceBase<T>
    {
        private readonly T _result;

        public FutureCompletionSourceResult(T result)
        {
            _result = result;
        }

        internal override T Result
        {
            get
            {
                return _result;
            }
        }

        internal override Exception Error
        {
            get
            {
                throw new InvalidOperationException();
            }
        }

        internal override bool IsCompleted
        {
            get
            {
                return true;
            }
        }

        internal override bool IsFaulted
        {
            get
            {
                return false;
            }
        }

        internal override void OnCompleted<R>(Action<Future<T>, R> continuation, R state)
        {
            continuation(new Future<T>(this), state);
        }

        internal override void OnCompleted<R>(Action<Future, R> continuation, R state)
        {
            continuation(new Future((FutureCompletionSource<object>)(object)this), state);
        }

        internal override void OnCompleted(Action continuation)
        {
            continuation();
        }

        public override void SetResult(T result)
        {
            throw new InvalidOperationException();
        }

        public override void SetException(Exception error)
        {
            throw new InvalidOperationException();
        }
    }
}

namespace System.Runtime.CompilerServices
{
    public struct AsyncFutureMethodBuilder<T>
    {
        private IAsyncStateMachine _stateMachine;
        private FutureCompletionSource<T> _fcs;

        public static AsyncFutureMethodBuilder<T> Create()
        {
            return default(AsyncFutureMethodBuilder<T>);
        }

        public Future<T> Task
        {
            get
            {
                if (_fcs == null)
                {
                    _fcs = new FutureCompletionSource<T>();
                }

                return _fcs.Future;
            }
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            AwaitOnCompleted(ref awaiter, ref stateMachine);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            if (_stateMachine == null)
            {
                var ignored = Task;
                _stateMachine = stateMachine;
                _stateMachine.SetStateMachine(_stateMachine);
            }

            awaiter.OnCompleted(_stateMachine.MoveNext);
        }

        public void SetException(Exception exception)
        {
            if (_fcs == null)
            {
                _fcs = new FutureCompletionSource<T>();
            }

            _fcs.SetException(exception);
        }

        public void SetResult(T result)
        {
            if (_fcs == null)
            {
                _fcs = GetCompletionForResult(result);
            }
            else
            {
                _fcs.SetResult(result);
            }
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            _stateMachine = stateMachine;
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        private FutureCompletionSource<T> GetCompletionForResult(T result)
        {
            // TODO: cache commonly used values

            var res = new FutureCompletionSource<T>();
            res.SetResult(result);
            return res;
        }
    }

    public struct AsyncFutureMethodBuilder
    {
        private IAsyncStateMachine _stateMachine;
        private FutureCompletionSourceBase<object> _fcs;

        public static AsyncFutureMethodBuilder Create()
        {
            return default(AsyncFutureMethodBuilder);
        }

        public Future Task
        {
            get
            {
                if (_fcs == null)
                {
                    _fcs = new FutureCompletionSource<object>();
                }

                return new Future(_fcs);
            }
        }

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            AwaitOnCompleted(ref awaiter, ref stateMachine);
        }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            if (_stateMachine == null)
            {
                var ignored = Task;
                _stateMachine = stateMachine;
                _stateMachine.SetStateMachine(_stateMachine);
            }

            awaiter.OnCompleted(_stateMachine.MoveNext);
        }

        public void SetException(Exception exception)
        {
            if (_fcs == null)
            {
                _fcs = new FutureCompletionSource<object>();
            }

            _fcs.SetException(exception);
        }

        public void SetResult()
        {
            if (_fcs == null)
            {
                _fcs = new FutureCompletionSourceResult<object>(null); // TODO: cache
            }
            else
            {
                _fcs.SetResult(null);
            }
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
            _stateMachine = stateMachine;
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }
    }
}
