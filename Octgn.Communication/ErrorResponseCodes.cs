﻿using System;

namespace Octgn.Communication
{
    public static class ErrorResponseCodes
    {
        public const string UnauthorizedRequest = nameof(UnauthorizedRequest);
        public const string UserOffline = nameof(UserOffline);
        public const string UnhandledServerError = nameof(UnhandledServerError);
        public const string AuthenticationFailed = nameof(AuthenticationFailed);
    }
}
