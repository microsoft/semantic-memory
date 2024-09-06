// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Microsoft.KernelMemory.MemoryDb.Qdrant.Client.Http;

internal sealed class Filter
{
    internal sealed class OrClause
    {
        [JsonPropertyName("should")]
        public List<object> Clauses { get; set; }

        internal OrClause()
        {
            this.Clauses = new();
        }

        internal OrClause Or(object condition)
        {
            this.Clauses.Add(condition);
            return this;
        }

        internal OrClause OrValue(string key, object value)
        {
            return this.Or(new MatchValueClause(key, value));
        }

        internal void Validate()
        {
            ArgumentNullExceptionEx.ThrowIfNull(this.Clauses, nameof(this.Clauses), "Filter clauses cannot be null");

            foreach (var x in this.Clauses)
            {
                switch (x)
                {
                    case AndClause ac:
                        ac.Validate();
                        break;

                    case OrClause oc:
                        oc.Validate();
                        break;

                    case MatchValueClause mvc:
                        mvc.Validate();
                        break;
                }
            }
        }
    }

    internal sealed class AndClause
    {
        [JsonPropertyName("must")]
        public List<object> Clauses { get; set; }

        internal AndClause()
        {
            this.Clauses = new();
        }

        internal AndClause And(object condition)
        {
            this.Clauses.Add(condition);
            return this;
        }

        internal AndClause AndValue(string key, object value)
        {
            return this.And(new MatchValueClause(key, value));
        }

        internal void Validate()
        {
            ArgumentNullExceptionEx.ThrowIfNull(this.Clauses, nameof(this.Clauses), "Filter clauses cannot be null");
            foreach (var x in this.Clauses)
            {
                switch (x)
                {
                    case AndClause ac:
                        ac.Validate();
                        break;

                    case OrClause oc:
                        oc.Validate();
                        break;

                    case MatchValueClause mvc:
                        mvc.Validate();
                        break;
                }
            }
        }
    }

    internal sealed class MustNotClause
    {
        [JsonPropertyName("must_not")]
        public List<MatchValueClause> Clauses { get; set; }

        public MustNotClause(string key, object value)
        {
            this.Clauses = new();
            this.Clauses.Add(new MatchValueClause(key, value));
        }

        internal void Validate()
        {
            this.Clauses.Single().Validate();
        }
    }

    internal sealed class MatchValueClause
    {
        [JsonPropertyName("key")]
        public string Key { get; set; }

        [JsonPropertyName("match")]
        public MatchValue Match { get; set; }

        public MatchValueClause()
        {
            this.Match = new();
            this.Key = string.Empty;
        }

        public MatchValueClause(string key, object value) : this()
        {
            this.Key = key;
            this.Match.Value = value;
        }

        internal void Validate()
        {
            ArgumentNullExceptionEx.ThrowIfNull(this.Key, nameof(this.Key), "Match filter key cannot be null");
            ArgumentNullExceptionEx.ThrowIfNull(this.Match, nameof(this.Match), "Match filter value cannot be null");
        }
    }

    internal sealed class MatchValue
    {
        [JsonPropertyName("value")]
        public object Value { get; set; }

        public MatchValue()
        {
            this.Value = string.Empty;
        }
    }
}
