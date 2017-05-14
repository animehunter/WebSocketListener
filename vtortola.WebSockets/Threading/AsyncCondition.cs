﻿using System;

namespace vtortola.WebSockets.Threading
{
    internal struct AsyncConditionVariable
    {
        private readonly AsyncConditionSource source;

        public bool IsSet => this.source != null && this.source.IsSet;

        public AsyncConditionVariable(AsyncConditionSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source), "source != null");

            this.source = source;
        }

        public AsyncConditionSource.Awaiter GetAwaiter()
        {
            if (this.source == null) throw new InvalidOperationException();

            return this.source.GetAwaiter();
        }

        public override string ToString()
        {
            return $"Condition: {this.IsSet}";
        }
    }
}