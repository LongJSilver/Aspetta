# Aspetta

**Aspetta** is a minimal .NET micro-library for debouncing and coalescing frequent triggers — ideal for delaying expensive or redundant operations like network syncs, UI updates, or event dispatches.

---

## ✨ Features

- Delay execution after a trigger
- Coalesce rapid triggers into a single execution
- Optional max delay span or trigger count limits
- Synchronous or asynchronous usage
- Runs on Task or dedicated thread
- Lightweight and dependency-free

## ❔ But Why?

Aspetta is perfect when:

- You need to debounce noisy events (file watchers, UI events, sensors)
- You want to batch changes and apply them all at once
- You need throttle-like control with clear limits

---
## Quick Example
``` csharp

//first and only execution will happen after about 1.5s
var delay = Defer
    .ToExecute(() => Console.WriteLine("Action executed!"))
    .ForAtLeast(500)
    .Build();

// Simulate a burst of triggers
for (int i = 0; i < 10; i++)
{
    delay.Trigger();
    Thread.Sleep(100);
}

```
This will only print **once**, about 500ms after the last trigger.

## ⏱ Limit Options

You can configure:

- MaximumDelaySpan: max total delay allowed,
- MaximumTriggerCount: max triggers before forced execution,
- or you can have both!

``` csharp
var delay = Defer
    .ToExecute(SaveData)
    .ForAtLeast(1000)
    .WithMaximumDelay(20000)
    .WithMaximumTriggerLimit(5)
    .Build();
```

## 🧵 Threading Options

You can choose whether the delay logic runs on:
- A background Task (default)
- A dedicated thread
``` csharp
var delay = Defer
    .UsingDedicatedThread()
    .ToExecute(ProcessBatch)
    .ForAtLeast(200)
    .Build();
```


## 📦 Install

Available on NuGet:
```sh
dotnet add package Aspetta
```

## 📜 License and credits

**Aspetta**  is released under Apache 2.0

The project icon (also used for the NuGet package) was designed by [Freepik](https://www.flaticon.com/authors/freepik)

Contributions are welcome.