﻿// Copyright (c) Microsoft. All rights reserved.

using System;

namespace Microsoft.KernelMemory.Evaluation;

public sealed partial class TestSetGenerator
{
    public struct Distribution : IEquatable<Distribution>
    {
        public float Simple { get; set; } = .5f;

        public float Reasoning { get; set; } = .16f;

        public float MultiContext { get; set; } = .17f;

        public float Conditioning { get; set; } = .17f;

        public Distribution() { }

        public override bool Equals(object? obj) => obj is Distribution distribution &&
                                                    this.Simple == distribution.Simple &&
                                                    this.Reasoning == distribution.Reasoning &&
                                                    this.MultiContext == distribution.MultiContext &&
                                                    this.Conditioning == distribution.Conditioning;

        public override int GetHashCode()
        {
            return HashCode.Combine(this.Simple, this.Reasoning, this.MultiContext, this.Conditioning);
        }

        public static bool operator ==(Distribution left, Distribution right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Distribution left, Distribution right)
        {
            return !(left == right);
        }

        public bool Equals(Distribution other)
        {
            return this == other;
        }
    }
}
