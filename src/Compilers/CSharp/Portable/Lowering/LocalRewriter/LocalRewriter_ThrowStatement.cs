﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class LocalRewriter
    {
        public override BoundNode VisitThrowStatement(BoundThrowStatement node)
        {

            var result = (BoundStatement)base.VisitThrowStatement(node)!;
            if (!_inExpressionLambda && this.Instrument && !node.WasCompilerGenerated)
            {
                result = _instrumenter.InstrumentThrowStatement(node, result);
            }

            return result;
        }
    }
}
