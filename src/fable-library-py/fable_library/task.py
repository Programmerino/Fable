"""Task handling.

This module implements .NET
[tasks](https://docs.microsoft.com/en-us/dotnet/standard/async-in-depth)
using Python async / await.
"""

from __future__ import annotations

import asyncio
from asyncio import AbstractEventLoop, Future
from collections.abc import Awaitable
from typing import Any, Generic, TypeVar


_T = TypeVar("_T")


class TaskCompletionSource(Generic[_T]):
    __slots__ = "future", "loop"

    def __init__(self) -> None:
        self.loop: AbstractEventLoop = asyncio.get_event_loop()
        self.future: Future[_T] = self.loop.create_future()

    def SetResult(self, value: _T) -> None:
        """Set result.

        Transitions the underlying Task[TResult] into the
        RanToCompletion state.
        """

        def action():
            self.future.set_result(value)

        self.loop.call_soon_threadsafe(action)

    def SetCancelled(self) -> None:
        """Set cancelled.

        Transitions the underlying Task[TResult] into the Canceled
        state.
        """

        def action():
            self.future.cancel()

        self.loop.call_soon_threadsafe(action)

    def SetException(self, exception: Exception) -> None:
        """Set exception.

        Transitions the underlying Task[TResult] into the Faulted state
        and binds it to a specified exception.
        """

        def action():
            self.future.set_exception(exception)

        self.loop.call_soon_threadsafe(action)

    def get_task(self) -> Awaitable[_T]:
        return asyncio.ensure_future(self.future)


async def zero() -> None:
    return


async def from_result(value: _T) -> _T:
    return value


def get_awaiter(value: Awaitable[_T]) -> Awaitable[_T]:
    # Wrap awaitable in coroutine (so we can run it using create_task)
    async def get_value() -> _T:
        return await asyncio.ensure_future(value)

    return get_value()


def get_result(value: Awaitable[_T]) -> _T:
    """Get the result.

    Ends the wait for the completion of the asynchronous task.
    """

    async def runner() -> _T:
        return await value

    return asyncio.run(runner())


def start(task: Awaitable[Any]) -> None:
    async def runner():
        return await task

    asyncio.create_task(runner())


def run_synchronously(task: Awaitable[Any]) -> None:
    async def runner():
        return await task

    asyncio.run(runner())


__all__ = [
    "TaskCompletionSource",
    "from_result",
    "get_awaiter",
    "get_result",
    "run_synchronously",
    "start",
    "zero",
]
