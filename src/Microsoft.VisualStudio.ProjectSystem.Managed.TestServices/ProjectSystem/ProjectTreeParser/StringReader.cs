﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Diagnostics;

namespace Microsoft.VisualStudio.ProjectSystem
{
    // Simple, cheap, forward-only string reader
    internal class StringReader
    {
        private readonly string _input;
        private int _position;

        public StringReader(string input)
            : this(input, 0)
        {
        }

        private StringReader(string input, int startIndex)
        {
            Assumes.NotNull(input);
            Assumes.True(input.Length > 0);
            Assumes.True(startIndex >= 0);
            Assumes.True(startIndex <= input.Length);

            _input = input;
            _position = startIndex;
        }

        public bool CanRead
        {
            get
            {
                if (_position < _input.Length)
                {
                    // Treat null as end of string
                    return PeekChar() != '\0';
                }

                return false;
            }
        }

        public char Peek()
        {
            Assumes.True(CanRead);

            return PeekChar();
        }

        public char Read()
        {
            char c = Peek();

            _position++;

            return c;
        }

        private char PeekChar()
        {
            return _input[_position];
        }

        public StringReader Clone()
        {
            return new StringReader(_input, _position);
        }
    }
}
