﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding;
using Microsoft.VisualStudio.Text.Editor.Commanding.Commands;

namespace Microsoft.CodeAnalysis.Editor.Implementation.InlineRename;

internal abstract partial class AbstractRenameCommandHandler :
    ICommandHandler<MoveSelectedLinesUpCommandArgs>, ICommandHandler<MoveSelectedLinesDownCommandArgs>
{
    public CommandState GetCommandState(MoveSelectedLinesUpCommandArgs args)
        => CommandState.Unspecified;

    public bool ExecuteCommand(MoveSelectedLinesUpCommandArgs args, CommandExecutionContext context)
        => HandleMoveSelectLinesUpOrDownCommand();

    public CommandState GetCommandState(MoveSelectedLinesDownCommandArgs args)
        => CommandState.Unspecified;

    public bool ExecuteCommand(MoveSelectedLinesDownCommandArgs args, CommandExecutionContext context)
        => HandleMoveSelectLinesUpOrDownCommand();

    private bool HandleMoveSelectLinesUpOrDownCommand()
    {
        // When rename commit is in progress, swallow the command so it won't change the workspace
        if (IsRenameCommitInProgress())
            return true;

        CancelRenameSession();
        return false;
    }
}
