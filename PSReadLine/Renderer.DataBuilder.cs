using System.Collections.Generic;
using System.Linq;
using System.Management.Automation.Language;
using System.Text;

namespace Microsoft.PowerShell;

public partial class Renderer
{
    private class DataBuilder
    {
        private string _text;
        private string _color;
        private string _activeColor = string.Empty;
        private bool _afterLastToken = false;
        private int _currentLogicalLine = 0;
        private bool _inSelectedRegion = false;

        private void BuildOneChar(char charToRender, bool toEmphasize)
        {
            if (charToRender == '\n')
            {
                if (_inSelectedRegion)
                    // Turn off inverse before end of line, turn on after continuation prompt
                    _consoleBufferLines[_currentLogicalLine].Append(VTColorUtils.AnsiReset);

                _currentLogicalLine += 1;
                if (_currentLogicalLine == _consoleBufferLines.Count)
                    _consoleBufferLines.Add(new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth));

                // Reset the color for continuation prompt so the color sequence will always be explicitly
                // specified for continuation prompt in the generated render strings.
                // This is necessary because we will likely not rewrite all texts during rendering, and thus
                // we cannot assume the continuation prompt can continue to use the active color setting from
                // the previous rendering string.
                _activeColor = string.Empty;

                if (_rl.Options.ContinuationPrompt.Length > 0)
                {
                    UpdateColorsIfNecessary(_rl.Options._continuationPromptColor);
                    _consoleBufferLines[_currentLogicalLine].Append(_rl.Options.ContinuationPrompt);
                }

                if (_inSelectedRegion)
                    // Turn off inverse before end of line, turn on after continuation prompt
                    _consoleBufferLines[_currentLogicalLine].Append(_rl.Options.SelectionColor);

                return;
            }

            UpdateColorsIfNecessary(toEmphasize ? _rl.Options._emphasisColor : _color);

            if (char.IsControl(charToRender))
            {
                _consoleBufferLines[_currentLogicalLine].Append('^');
                _consoleBufferLines[_currentLogicalLine].Append((char) ('@' + charToRender));
            }
            else
            {
                _consoleBufferLines[_currentLogicalLine].Append(charToRender);
            }
        }

        private void UpdateColorsIfNecessary(string newColor)
        {
            if (!ReferenceEquals(newColor, _activeColor))
            {
                if (!_inSelectedRegion)
                    _consoleBufferLines[_currentLogicalLine]
                        .Append(VTColorUtils.AnsiReset)
                        .Append(newColor);

                _activeColor = newColor;
            }
        }

        private List<StringBuilder> _consoleBufferLines = new(1)
            {new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth)};

        public List<StringBuilder> Generate(string defaultColor)
        {
            _text = _rl.buffer.ToString();
            _rl._Prediction.QueryForSuggestion(_text);
            _color = defaultColor;

            var tokenStack = new Stack<Renderer.SavedTokenState>();
            tokenStack.Push(new Renderer.SavedTokenState
            {
                Tokens = _rl.Tokens,
                Index = 0,
                Color = defaultColor
            });

            var selectionStart = -1;
            var selectionEnd = -1;
            if (_rl._visualSelectionCommandCount > 0)
            {
                _renderer.GetRegion(out var regionStart, out var regionLength);
                if (regionLength > 0)
                {
                    selectionStart = regionStart;
                    selectionEnd = selectionStart + regionLength;
                }
            }

            for (var i = 0; i < _text.Length; i++)
            {
                if (i == selectionStart)
                {
                    _consoleBufferLines[_currentLogicalLine].Append(_rl.Options.SelectionColor);
                    _inSelectedRegion = true;
                }
                else if (i == selectionEnd)
                {
                    _consoleBufferLines[_currentLogicalLine].Append(VTColorUtils.AnsiReset);
                    _consoleBufferLines[_currentLogicalLine].Append(_activeColor);
                    _inSelectedRegion = false;
                }

                if (!_afterLastToken)
                {
                    // Figure out the color of the character - if it's in a token,
                    // use the tokens color otherwise use the initial color.
                    var state = tokenStack.Peek();
                    var token = state.Tokens[state.Index];
                    while (i == token.Extent.EndOffset)
                    {
                        if (state.Index == state.Tokens.Length - 1)
                        {
                            tokenStack.Pop();
                            if (tokenStack.Count == 0)
                            {
                                _afterLastToken = true;
                                token = null;
                                _color = defaultColor;
                                break;
                            }

                            state = tokenStack.Peek();

                            // It's possible that a 'StringExpandableToken' is the last available token, for example:
                            //   'begin $a\abc def', 'process $a\abc | blah' and 'end $a\abc; hello'
                            // due to the special handling of the keywords 'begin', 'process' and 'end', all the above 3 script inputs
                            // generate only 2 tokens by the parser -- A KeywordToken, and a StringExpandableToken '$a\abc'. Text after
                            // '$a\abc' is not tokenized at all.
                            // We repeat the test to see if we fall into this case ('token' is the final one in the stack).
                            continue;
                        }

                        _color = state.Color;
                        token = state.Tokens[++state.Index];
                    }

                    if (!_afterLastToken && i == token.Extent.StartOffset)
                    {
                        _color = _renderer.GetTokenColor(token);

                        if (token is StringExpandableToken stringToken)
                            // We might have nested tokens.
                            if (stringToken.NestedTokens != null && stringToken.NestedTokens.Any())
                            {
                                var tokens = new Token[stringToken.NestedTokens.Count + 1];
                                stringToken.NestedTokens.CopyTo(tokens, 0);
                                // NestedTokens doesn't have an "EOS" token, so we use
                                // the string literal token for that purpose.
                                tokens[tokens.Length - 1] = stringToken;

                                tokenStack.Push(new Renderer.SavedTokenState
                                {
                                    Tokens = tokens,
                                    Index = 0,
                                    Color = _color
                                });

                                if (i == tokens[0].Extent.StartOffset) _color = _renderer.GetTokenColor(tokens[0]);
                            }
                    }
                }

                var charToRender = _text[i];
                var toEmphasize = i >= _renderer.EmphasisStart &&
                                  i < _renderer.EmphasisStart + _renderer.EmphasisLength;

                BuildOneChar(charToRender, toEmphasize);
            }

            if (_inSelectedRegion)
            {
                _consoleBufferLines[_currentLogicalLine].Append(VTColorUtils.AnsiReset);
                _inSelectedRegion = false;
            }

            _rl._Prediction.ActiveView.RenderSuggestion(_consoleBufferLines, ref _currentLogicalLine);
            _activeColor = string.Empty;

            if (!string.IsNullOrEmpty(_renderer.StatusLinePrompt))
            {
                _currentLogicalLine += 1;
                if (_currentLogicalLine > _consoleBufferLines.Count - 1)
                    _consoleBufferLines.Add(new StringBuilder(PSConsoleReadLineOptions.CommonWidestConsoleWidth));

                _color = _rl._statusIsErrorMessage ? _rl.Options._errorColor : defaultColor;
                UpdateColorsIfNecessary(_color);

                foreach (var c in _renderer.StatusLinePrompt) _consoleBufferLines[_currentLogicalLine].Append(c);

                _consoleBufferLines[_currentLogicalLine].Append(_renderer.StatusBuffer);
            }

            return _consoleBufferLines;
        }
    }
}