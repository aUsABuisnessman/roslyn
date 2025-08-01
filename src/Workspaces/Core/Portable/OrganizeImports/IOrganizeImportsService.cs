﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.CodeAnalysis.OrganizeImports;

internal interface IOrganizeImportsService : ILanguageService
{
    Task<Document> OrganizeImportsAsync(Document document, OrganizeImportsOptions options, CancellationToken cancellationToken);

    string SortImportsDisplayStringWithAccelerator { get; }
    string SortImportsDisplayStringWithoutAccelerator { get; }

    string SortAndRemoveUnusedImportsDisplayStringWithAccelerator { get; }
}
