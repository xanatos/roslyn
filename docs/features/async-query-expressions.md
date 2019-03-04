# Async Query Expressions

Prototype of support for `await` expressions in query expressions. For example:

```csharp
from x in xs
where x > 0 && await f(x)
select new Foo { Bar = await g(x) }
```

## Syntax

Syntactic options to support this are:

1. Require the query to start with `async from` instead of `from`.
   * May be good for explicitness.
   * Note that it shouldn't imply that all clauses have to be async (i.e. use `await`).
2. Just allow `await` in any clause and generate async lambdas for clauses.
   * Implicit variant of the above, thus unlike explicit `async` in methods or lambdas.
   * Quite clear that clauses can be sync or async.
3. Have some `async` modifier in various clauses, e.g. `async where`, `async select`.
   * Gets quite verbose, e.g. `async where await f(x)`.
   * Not all generated lambdas have a good syntactic spot to squeeze in an `async`:
     - E.g. `from x in xs join y in ys on e1 equals e2 select v` has lambdas for `e1` and `e2`.
     - Would one need `async on`, `async equals`, `async select`?

The code in this branch implements technique 2, but it'd be easy to require `async from` to opt in to using `await` in clauses.

## Binding

Query expressions are specified as syntactic sugar over method invocations for standard query operators such as `Where`, `Select`, etc. To bind async query clauses, we have a few options:

1. Use the same method names that already exist.
   * E.g. `from x in xs select await f(x)` becomes `xs.Select(async x => await f(x))`.
   * Possible problem is that `from x in xs select f(x)`, where `f(x)` returns a task-like type **doesn't** return a sequence of tasks, but implicitly awaits them.
2. Use a variation on the existing method names to indicate the async variant.
   * This name is used whenever **any** clause's generated lambda expression is `async`.
   * E.g. `SelectAsync` or `SelectAwait`.

The code in this branch implements strategy 1 and 2, using a conditional `#if QUERY_METHOD_USE_AWAIT_SUFFIX` check so both can be compared against library implementations to assess issues such as ambiguities, unexpected awaits, overload resolution gotchas, etc.

For strategy 2, we use an `Await` suffix rather than `Async` because query operator libraries for `IAsyncEnumerable<T>` may have methods such as `SumAsync` which return a task-like type. These may also have overloads with asynchronous selector functions, and indicating that (for sake of consistency with other query operator methods) in the name would lead to `SumAsyncAsync`. With our naming convention, it becomes `SumAwaitAsync`. Some examples are shown below:

```csharp
static IAsyncEnumerable<TResult> Select<TSource, TResult>(this IAsyncEnumerable<TSource> source, Func<TSource, TResult> selector);
static IAsyncEnumerable<TResult> SelectAwait<TSource, TResult>(this IAsyncEnumerable<TSource> source, Func<TSource, ValueTask<TResult>> selector);

static IAsyncEnumerable<int> SumAsync<TSource>(this IAsyncEnumerable<TSource> source, Func<T, int> selector);
static IAsyncEnumerable<int> SumAwaitAsync<TSource>(this IAsyncEnumerable<TSource> source, Func<T, ValueTask<int>> selector);
```

Another problem with binding is the need for many variants to support all combinations of synchronous and asynchronous clauses, i.e. `2^N` variants per operator, where `N` is the number of delegate-typed parameters on the operator. (Note the use of *variant* rather than *overload* because strategy 2 splits these in two sets of *overloads* depending on whether any parameter is an async delegate, i.e. `1 + (2^N - 1)`.)

For example:

* `Where`, `Select`, `OrderBy`, and `ThenBy` have 1 delegate parameter, so need 2 variants.
* `SelectMany` and `GroupBy` have 2 delegate parameters, so need 4 variants.
* `Join` and `GroupJoin` have 3 delegate parameters, so need 8 variants.

Note that these only include variants that are the target of query expression binding. LINQ libraries tend to have more variants of these operators, so for consistency one may want to have the same combinations for all of these. This also excludes any variants that take a `CancellationToken` on the delegate types to support "deep cancellation" wired into predicates, selectors, etc.

## Example

Our experimental implementation of async query expressions allows compiling the following example code:

```csharp
#define QUERY_METHOD_USE_AWAIT_SUFFIX // NB: Align with option used to build Roslyn

using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static void Main() {}

    static I<G<int, int>> GroupBy1(I<int> xs) => from x in xs group x + 1 by x * 2;
    static I<G<int, int>> GroupBy2(I<int> xs) => from x in xs group await Task.FromResult(x) + 1 by x * 2;
    static I<G<int, int>> GroupBy3(I<int> xs) => from x in xs group x + 1 by await Task.FromResult(x) * 2;
    static I<G<int, int>> GroupBy4(I<int> xs) => from x in xs group await Task.FromResult(x) + 1 by await Task.FromResult(x) * 2;

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<G<int, int>> GroupBy5(I<int> xs) => from x in xs group Task.FromResult(x + 1) by x * 2; // REVIEW: Undesirable flattening?
    static I<G<int, int>> GroupBy6(I<int> xs) => from x in xs group x + 1 by Task.FromResult(x * 2); // REVIEW: Undesirable flattening?
#else
    static I<G<int, Task<int>>> GroupBy5(I<int> xs) => from x in xs group Task.FromResult(x + 1) by x * 2;
    static I<G<Task<int>, int>> GroupBy6(I<int> xs) => from x in xs group x + 1 by Task.FromResult(x * 2);
#endif

    static I<int> GroupJoin1(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y into g select x + g.Key;
    static I<int> GroupJoin2(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y into g select await Task.FromResult(x + g.Key);
    static I<int> GroupJoin3(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals await Task.FromResult(y) into g select x + g.Key;
    static I<int> GroupJoin4(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals await Task.FromResult(y) into g select await Task.FromResult(x + g.Key);
    static I<int> GroupJoin5(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals y into g select x + g.Key;
    static I<int> GroupJoin6(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals y into g select await Task.FromResult(x + g.Key);
    static I<int> GroupJoin7(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals Task.FromResult(y) into g select x + g.Key;
    static I<int> GroupJoin8(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals Task.FromResult(y) into g select await Task.FromResult(x + g.Key);

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> GroupJoin9(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y into g select Task.FromResult(x + g.Key); // REVIEW: Undesirable flattening?
#else
    static I<Task<int>> GroupJoin9(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y into g select Task.FromResult(x + g.Key);
#endif

    static I<int> Join1(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y select x + y;
    static I<int> Join2(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y select await Task.FromResult(x + y);
    static I<int> Join3(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals await Task.FromResult(y) select x + y;
    static I<int> Join4(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals await Task.FromResult(y) select await Task.FromResult(x + y);
    static I<int> Join5(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals y select x + y;
    static I<int> Join6(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals y select await Task.FromResult(x + y);
    static I<int> Join7(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals Task.FromResult(y) select x + y;
    static I<int> Join8(I<int> xs, I<int> ys) => from x in xs join y in ys on await Task.FromResult(x) equals Task.FromResult(y) select await Task.FromResult(x + y);

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> Join9(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y select Task.FromResult(x + y); // REVIEW: Undesirable flattening?
#else
    static I<Task<int>> Join9(I<int> xs, I<int> ys) => from x in xs join y in ys on x equals y select Task.FromResult(x + y);
#endif

    static I<int> OrderBy1(I<int> xs) => from x in xs orderby x select x;
    static I<int> OrderBy2(I<int> xs) => from x in xs orderby x descending select x;
    static I<int> OrderBy3(I<int> xs) => from x in xs orderby await Task.FromResult(x) select x;
    static I<int> OrderBy4(I<int> xs) => from x in xs orderby await Task.FromResult(x) descending select x;

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> OrderBy5(I<int> xs) => from x in xs orderby Task.FromResult(x) select x; // REVIEW: Undesirable flattening?
    static I<int> OrderBy6(I<int> xs) => from x in xs orderby Task.FromResult(x) descending select x; // REVIEW: Undesirable flattening?
#else
    // NB: These are ordering by Task<T> values using the default comparer.
    static I<int> OrderBy5(I<int> xs) => from x in xs orderby Task.FromResult(x) select x;
    static I<int> OrderBy6(I<int> xs) => from x in xs orderby Task.FromResult(x) descending select x;
#endif

    static I<int> ThenBy1(I<int> xs) => from x in xs orderby x, x select x;
    static I<int> ThenBy2(I<int> xs) => from x in xs orderby x, x descending select x;
    static I<int> ThenBy3(I<int> xs) => from x in xs orderby x, await Task.FromResult(x) select x;
    static I<int> ThenBy4(I<int> xs) => from x in xs orderby x, await Task.FromResult(x) descending select x;

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> ThenBy5(I<int> xs) => from x in xs orderby x, Task.FromResult(x) select x; // REVIEW: Undesirable flattening?
    static I<int> ThenBy6(I<int> xs) => from x in xs orderby x, Task.FromResult(x) descending select x; // REVIEW: Undesirable flattening?
#else
    // NB: These are ordering by Task<T> values using the default comparer.
    static I<int> ThenBy5(I<int> xs) => from x in xs orderby x, Task.FromResult(x) select x;
    static I<int> ThenBy6(I<int> xs) => from x in xs orderby x, Task.FromResult(x) descending select x;
#endif

    static I<int> Select1(I<int> xs) => from x in xs select x;
    static I<int> Select2(I<int> xs) => from x in xs select x + 1;
    static I<int> Select3(I<int> xs) => from x in xs select await Task.FromResult(x) + 1;

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> Select4(I<int> xs) => from x in xs select Task.FromResult(x + 1); // REVIEW: Undesirable flattening?
#else
    static I<Task<int>> Select4(I<int> xs) => from x in xs select Task.FromResult(x + 1);
#endif

    static I<int> SelectMany1(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(x) select x + y;
    static I<int> SelectMany2(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(await Task.FromResult(x)) select x + y;
    static I<int> SelectMany3(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(x) select await Task.FromResult(x) + y;
    static I<int> SelectMany4(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(await Task.FromResult(x)) select await Task.FromResult(x) + y;

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> SelectMany5(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(x) select Task.FromResult(x + y); // REVIEW: Undesirable flattening?
    static I<int> SelectMany6(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(await Task.FromResult(x)) select Task.FromResult(x + y); // REVIEW: Undesirable flattening?
    static I<int> SelectMany7(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in Task.FromResult(ys(x)) select x + y; // REVIEW: Undesirable flattening?
#else
    static I<Task<int>> SelectMany5(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(x) select Task.FromResult(x + y);
    static I<int> SelectMany6(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in ys(await Task.FromResult(x)) select Task.FromResult(x + y); // REVIEW: Undesirable flattening?
    // static I<int> SelectMany7(I<int> xs, Func<int, I<int>> ys) => from x in xs from y in Task.FromResult(ys(x)) select x + y; // NB: Doesn't compile.
#endif

    static I<int> Where1(I<int> xs) => from x in xs where x > 0 select x;
    static I<int> Where2(I<int> xs) => from x in xs where await Task.FromResult(x) > 0 select x;

#if !QUERY_METHOD_USE_AWAIT_SUFFIX
    static I<int> Where3(I<int> xs) => from x in xs where Task.FromResult(x > 0) select x; // REVIEW: Undesirable flattening?
#endif
}

interface I<T>
{
}

interface O<T>: I<T>
{
}

interface G<K, T> : I<T>
{
    K Key { get; }
}

static class I
{
    public static I<G<K, E>> GroupBy<T, K, E>(this I<T> source, Func<T, K> keySelector, Func<T, E> elementSelector) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static I<G<K, E>> GroupByAwait<T, K, E>(this I<T> source, Func<T, Task<K>> keySelector, Func<T, E> elementSelector) => throw new NotImplementedException();
    public static I<G<K, E>> GroupByAwait<T, K, E>(this I<T> source, Func<T, K> keySelector, Func<T, Task<E>> elementSelector) => throw new NotImplementedException();
    public static I<G<K, E>> GroupByAwait<T, K, E>(this I<T> source, Func<T, Task<K>> keySelector, Func<T, Task<E>> elementSelector) => throw new NotImplementedException();
#else
    public static I<G<K, E>> GroupBy<T, K, E>(this I<T> source, Func<T, Task<K>> keySelector, Func<T, E> elementSelector) => throw new NotImplementedException();
    public static I<G<K, E>> GroupBy<T, K, E>(this I<T> source, Func<T, K> keySelector, Func<T, Task<E>> elementSelector) => throw new NotImplementedException();
    public static I<G<K, E>> GroupBy<T, K, E>(this I<T> source, Func<T, Task<K>> keySelector, Func<T, Task<E>> elementSelector) => throw new NotImplementedException();
#endif
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
#else
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, R> resultSelector) => throw new NotImplementedException();
    public static I<R> GroupJoin<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, G<K, T2>, Task<R>> resultSelector) => throw new NotImplementedException();
#endif

    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();
    public static I<R> JoinAwait<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
#else
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, K> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, K> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, R> resultSelector) => throw new NotImplementedException();
    public static I<R> Join<T1, T2, K, R>(this I<T1> source1, I<T2> source2, Func<T1, Task<K>> keySelector1, Func<T2, Task<K>> keySelector2, Func<T1, T2, Task<R>> resultSelector) => throw new NotImplementedException();
#endif

    public static O<T> OrderBy<T, K>(this I<T> source, Func<T, K> keySelector) => throw new NotImplementedException();
    public static O<T> OrderByDescending<T, K>(this I<T> source, Func<T, K> keySelector) => throw new NotImplementedException();
    public static O<T> ThenBy<T, K>(this O<T> source, Func<T, K> keySelector) => throw new NotImplementedException();
    public static O<T> ThenByDescending<T, K>(this O<T> source, Func<T, K> keySelector) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static O<T> OrderByAwait<T, K>(this I<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
    public static O<T> OrderByDescendingAwait<T, K>(this I<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
    public static O<T> ThenByAwait<T, K>(this O<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
    public static O<T> ThenByDescendingAwait<T, K>(this O<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
#else
    public static O<T> OrderBy<T, K>(this I<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
    public static O<T> OrderByDescending<T, K>(this I<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
    public static O<T> ThenBy<T, K>(this O<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
    public static O<T> ThenByDescending<T, K>(this O<T> source, Func<T, Task<K>> keySelector) => throw new NotImplementedException();
#endif

    public static I<R> Select<T, R>(this I<T> source, Func<T, R> selector) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static I<R> SelectAwait<T, R>(this I<T> source, Func<T, Task<R>> selector) => throw new NotImplementedException();
#else
    public static I<R> Select<T, R>(this I<T> source, Func<T, Task<R>> selector) => throw new NotImplementedException();
#endif

    public static I<R> SelectMany<T, C, R>(this I<T> source, Func<T, I<C>> collectionSelector, Func<T, C, R> resultSelector) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static I<R> SelectManyAwait<T, C, R>(this I<T> source, Func<T, Task<I<C>>> collectionSelector, Func<T, C, R> resultSelector) => throw new NotImplementedException();
    public static I<R> SelectManyAwait<T, C, R>(this I<T> source, Func<T, I<C>> collectionSelector, Func<T, C, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> SelectManyAwait<T, C, R>(this I<T> source, Func<T, Task<I<C>>> collectionSelector, Func<T, C, Task<R>> resultSelector) => throw new NotImplementedException();
#else
    public static I<R> SelectMany<T, C, R>(this I<T> source, Func<T, Task<I<C>>> collectionSelector, Func<T, C, R> resultSelector) => throw new NotImplementedException();
    public static I<R> SelectMany<T, C, R>(this I<T> source, Func<T, I<C>> collectionSelector, Func<T, C, Task<R>> resultSelector) => throw new NotImplementedException();
    public static I<R> SelectMany<T, C, R>(this I<T> source, Func<T, Task<I<C>>> collectionSelector, Func<T, C, Task<R>> resultSelector) => throw new NotImplementedException();
#endif

    public static I<T> Where<T>(this I<T> source, Func<T, bool> predicate) => throw new NotImplementedException();

#if QUERY_METHOD_USE_AWAIT_SUFFIX
    public static I<T> WhereAwait<T>(this I<T> source, Func<T, Task<bool>> predicate) => throw new NotImplementedException();
#else
    public static I<T> Where<T>(this I<T> source, Func<T, Task<bool>> predicate) => throw new NotImplementedException();
#endif
}
```
