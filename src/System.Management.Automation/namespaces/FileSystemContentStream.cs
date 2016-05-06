/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Provider;
using System.Threading.Tasks;
using Dbg=System.Management.Automation;

namespace Microsoft.PowerShell.Commands
{
    /// <summary>
    /// The content stream class for the file system provider. It implements both
    /// the IContentReader and IContentWriter interfaces.
    /// </summary>
    /// 
    /// <remarks>
    /// Note, this class does no specific error handling. All errors are allowed to
    /// propogate to the caller so that they can be written to the error pipeline
    /// if necessary.
    /// </remarks>
    /// 
    internal class FileSystemContentReaderWriter : IContentReader, IContentWriter
    {
        #region tracer

        /// <summary>
        /// An instance of the PSTraceSource class used for trace output
        /// using "FileSystemContentStream" as the category.
        /// </summary>
        [Dbg.TraceSourceAttribute(
            "FileSystemContentStream", 
            "The provider content reader and writer for the file system")]
        private static Dbg.PSTraceSource tracer =
            Dbg.PSTraceSource.GetTracer ("FileSystemContentStream",
            "The provider content reader and writer for the file system");

        #endregion tracer

        private string path;
        private string streamName;
        private FileMode mode;
        private FileAccess access;
        private FileShare share;
        private Encoding encoding;
        private CmdletProvider provider;

        private FileStream stream;
        private StreamReader reader;
        private StreamWriter writer;
        private bool usingByteEncoding;
        private string delimiter = "\n";
        private bool usingDelimiter;
        private bool waitForChanges;
        private bool isRawStream;
        private long fileOffset;

        private FileAttributes oldAttributes;
        private bool haveOldAttributes;

        // The reader to read file content backward
        private FileStreamBackReader _backReader;
        private bool _alreadyDetectEncoding = false;

        // False to add a newline to the end of the output string, true if not.
        private bool suppressNewline = false;

        /// <summary>
        /// Constructor for the content stream
        /// </summary>
        /// 
        /// <param name="path">
        /// The path to the file to get the content from.
        /// </param>
        /// 
        /// <param name="mode">
        /// The file mode to open the file with.
        /// </param>
        /// 
        /// <param name="access">
        /// The file access requested in the file.
        /// </param>
        /// 
        /// <param name="share">
        /// The file share to open the file with
        /// </param>
        ///
        /// <param name="encoding">
        /// The encoding of the file to be read or written.
        /// </param>
        /// 
        /// <param name="usingByteEncoding">
        /// If true, bytes will be read from the file. If false, the specified encoding
        /// will be used to read the file.
        /// </param>
        /// 
        /// <param name="waitForChanges">
        /// If true, we will perform blocking reads on the file, waiting for new content to be appended
        /// </param>
        /// 
        /// <param name="provider">
        /// The CmdletProvider invoking this stream
        /// </param>
        /// 
        /// <param name="isRawStream">
        /// Indicates raw stream.
        /// </param>
        /// 
        public FileSystemContentReaderWriter(
            string path, FileMode mode, FileAccess access,
            FileShare share, Encoding encoding, bool usingByteEncoding,
            bool waitForChanges, CmdletProvider provider, bool isRawStream) : 
                this(path, null, mode, access, share, encoding, usingByteEncoding, waitForChanges, provider, isRawStream)
        {
        }

        /// <summary>
        /// Constructor for the content stream
        /// </summary>
        /// 
        /// <param name="path">
        /// The path to the file to get the content from.
        /// </param>
        /// 
        /// <param name="streamName">
        /// The name of the Alternate Data Stream to get the content from. If null or empty, returns
        /// the file's primary content.
        /// </param>
        /// 
        /// <param name="mode">
        /// The file mode to open the file with.
        /// </param>
        /// 
        /// <param name="access">
        /// The file access requested in the file.
        /// </param>
        /// 
        /// <param name="share">
        /// The file share to open the file with
        /// </param>
        ///
        /// <param name="encoding">
        /// The encoding of the file to be read or written.
        /// </param>
        /// 
        /// <param name="usingByteEncoding">
        /// If true, bytes will be read from the file. If false, the specified encoding
        /// will be used to read the file.
        /// </param>
        /// 
        /// <param name="waitForChanges">
        /// If true, we will perform blocking reads on the file, waiting for new content to be appended
        /// </param>
        /// 
        /// <param name="provider">
        /// The CmdletProvider invoking this stream
        /// </param>
        /// 
        /// <param name="isRawStream">
        /// Indicates raw stream.
        /// </param>
        /// 
        public FileSystemContentReaderWriter(
            string path, string streamName, FileMode mode, FileAccess access, FileShare share,
            Encoding encoding, bool usingByteEncoding, bool waitForChanges, CmdletProvider provider,
            bool isRawStream)
        {
            if (String.IsNullOrEmpty(path))
            {
                throw PSTraceSource.NewArgumentNullException("path");
            }

            if (tracer.IsEnabled)
            {
                tracer.WriteLine("path = {0}", path);
                tracer.WriteLine("mode = {0}", mode);
                tracer.WriteLine("access = {0}", access);
            }

            this.path = path;
            this.streamName = streamName;
            this.mode = mode;
            this.access = access;
            this.share = share;
            this.encoding = encoding;
            this.usingByteEncoding = usingByteEncoding;
            this.waitForChanges = waitForChanges;
            this.provider = provider;
            this.isRawStream = isRawStream;

            CreateStreams(path, streamName, mode, access, share, encoding);
        }

        /// <summary>
        /// Constructor for the content stream
        /// </summary>
        /// 
        /// <param name="path">
        /// The path to the file to get the content from.
        /// </param>
        /// 
        /// <param name="streamName">
        /// The name of the Alternate Data Stream to get the content from. If null or empty, returns
        /// the file's primary content.
        /// </param>
        /// 
        /// <param name="mode">
        /// The file mode to open the file with.
        /// </param>
        /// 
        /// <param name="access">
        /// The file access requested in the file.
        /// </param>
        /// 
        /// <param name="share">
        /// The file share to open the file with
        /// </param>
        ///
        /// <param name="encoding">
        /// The encoding of the file to be read or written.
        /// </param>
        /// 
        /// <param name="usingByteEncoding">
        /// If true, bytes will be read from the file. If false, the specified encoding
        /// will be used to read the file.
        /// </param>
        /// 
        /// <param name="waitForChanges">
        /// If true, we will perform blocking reads on the file, waiting for new content to be appended
        /// </param>
        /// 
        /// <param name="provider">
        /// The CmdletProvider invoking this stream
        /// </param>
        /// 
        /// <param name="isRawStream">
        /// Indicates raw stream.
        /// </param>
        /// 
        /// <param name="suppressNewline">
        /// False to add a newline to the end of the output string, true if not.
        /// </param>
        /// 
        public FileSystemContentReaderWriter(
            string path, string streamName, FileMode mode, FileAccess access, FileShare share,
            Encoding encoding, bool usingByteEncoding, bool waitForChanges, CmdletProvider provider,
            bool isRawStream, bool suppressNewline)
                : this(path, streamName, mode, access, share, encoding, usingByteEncoding, waitForChanges, provider, isRawStream)
        {
            this.suppressNewline = suppressNewline;
        }

        /// <summary>
        /// Constructor for the content stream
        /// </summary>
        /// 
        /// <param name="path">
        /// The path to the file to get the content from.
        /// </param>
        /// 
        /// <param name="mode">
        /// The file mode to open the file with.
        /// </param>
        /// 
        /// <param name="access">
        /// The file access requested in the file.
        /// </param>
        /// 
        ///  <param name="share">
        ///    The file share to open the file with
        ///  </param>
        /// 
        /// <param name="delimiter">
        /// The delimiter to use when reading strings. Each time read is called, all contents up to an including
        /// the delimiter is read.
        /// </param>
        /// 
        /// <param name="encoding">
        /// The encoding of the file to be read or written.
        /// </param>
        /// 
        /// <param name="waitForChanges">
        /// If true, we will perform blocking reads on the file, waiting for new content to be appended
        /// </param>
        /// 
        /// <param name="provider">
        /// The CmdletProvider invoking this stream
        /// </param>
        /// 
        /// <param name="isRawStream">
        /// Indicates raw stream.
        /// </param>
        /// 
        public FileSystemContentReaderWriter(
            string path, 
            FileMode mode, 
            FileAccess access,
            FileShare  share,
            string delimiter, 
            Encoding encoding,
            bool waitForChanges,
            CmdletProvider provider,
            bool isRawStream) : this(path, null, mode, access, share, encoding, false, waitForChanges, provider, isRawStream)
        {
        }

        /// <summary>
        /// Constructor for the content stream
        /// </summary>
        /// 
        /// <param name="path">
        /// The path to the file to get the content from.
        /// </param>
        /// 
        /// <param name="streamName">
        /// The name of the Alternate Data Stream to get the content from. If null or empty, returns
        /// the file's primary content.
        /// </param>
        /// 
        /// <param name="mode">
        /// The file mode to open the file with.
        /// </param>
        /// 
        /// <param name="access">
        /// The file access requested in the file.
        /// </param>
        /// 
        ///  <param name="share">
        ///    The file share to open the file with
        ///  </param>
        /// 
        /// <param name="delimiter">
        /// The delimiter to use when reading strings. Each time read is called, all contents up to an including
        /// the delimiter is read.
        /// </param>
        /// 
        /// <param name="encoding">
        /// The encoding of the file to be read or written.
        /// </param>
        /// 
        /// <param name="waitForChanges">
        /// If true, we will perform blocking reads on the file, waiting for new content to be appended
        /// </param>
        /// 
        /// <param name="provider">
        /// The CmdletProvider invoking this stream
        /// </param>
        /// 
        /// <param name="isRawStream">
        /// Indicates raw stream.
        /// </param>
        /// 
        public FileSystemContentReaderWriter(
            string path,
            string streamName,
            FileMode mode,
            FileAccess access,
            FileShare share,
            string delimiter,
            Encoding encoding,
            bool waitForChanges,
            CmdletProvider provider,
            bool isRawStream)
            : this(path, streamName, mode, access, share, encoding, false, waitForChanges, provider, isRawStream)
        {
            this.delimiter = delimiter;
            this.usingDelimiter = true;
        }


        /// <summary>
        /// Reads the specified number of characters or a lines from the file.
        /// </summary>
        /// 
        /// <param name="readCount">
        /// If less than 1, then the entire file is read at once. If 1 or greater, then
        /// readCount is used to determine how many items (ie: lines, bytes, delimited tokens)
        /// to read per call.
        /// </param>
        /// 
        /// <returns>
        /// An array of strings representing the character(s) or line(s) read from
        /// the file.
        /// </returns>
        /// 
        public IList Read(long readCount)
        {
            if (isRawStream && waitForChanges)
            {
                throw PSTraceSource.NewInvalidOperationException(FileSystemProviderStrings.RawAndWaitCannotCoexist);
            }

            bool waitChanges = this.waitForChanges;

            tracer.WriteLine("blocks requested = {0}", readCount);

            ArrayList blocks = new ArrayList();
            bool readToEnd = (readCount <= 0);

            if (_alreadyDetectEncoding && reader.BaseStream.Position == 0)
            {
                Encoding curEncoding = reader.CurrentEncoding;
                // Close the stream, and reopen the stream to make the BOM correctly processed. 
                // The reader has already detected encoding, so if we don't reopen the stream, the BOM (if there is any)
                // will be treated as a regular character.
                stream.Dispose();
                CreateStreams(this.path, null, this.mode, this.access, this.share, curEncoding);
                _alreadyDetectEncoding = false;
            }

            try
            {
                for (long currentBlock = 0; (currentBlock < readCount) || (readToEnd); ++currentBlock)
                {
                    if (waitChanges && provider.Stopping)
                        waitChanges = false;
                    
                    if (usingByteEncoding)
                    {
                        if (!ReadByteEncoded(waitChanges, blocks, false))
                            break;
                    }
                    else
                    {
                        if (usingDelimiter || isRawStream)
                        {
                            if (!ReadDelimited(waitChanges, blocks, false, delimiter))
                                break;
                        }
                        else
                        {
                            if (!ReadByLine(waitChanges, blocks, false))
                                break;
                        }
                    }
                }

                tracer.WriteLine("blocks read = {0}", blocks.Count);
            }
            catch (Exception e)
            {
                if ((e is IOException) ||
                    (e is ArgumentException) ||
                    (e is System.Security.SecurityException) ||
                    (e is UnauthorizedAccessException) ||
                    (e is ArgumentNullException))
                {
                    //Exception contains specific message about the error occured and so no need for errordetails.
                    this.provider.WriteError(new ErrorRecord(e, "GetContentReaderIOError", ErrorCategory.ReadError, path));
                    return null;
                }
                else
                    throw;
            }

            return blocks.ToArray();
        }

        /// <summary>
        /// Read the content regardless of the 'waitForChanges' flag
        /// </summary>
        /// <param name="readCount"></param>
        /// <returns></returns>
        internal IList ReadWithoutWaitingChanges(long readCount)
        {
            bool oldWaitChanges = this.waitForChanges;
            this.waitForChanges = false;
            try
            {
                return Read(readCount);
            }
            finally
            {
                this.waitForChanges = oldWaitChanges;
            }
        }

        /// <summary>
        /// Move the pointer of the stream to the position where there are 'backCount' number
        /// of items (depends on what we are using: delimiter? line? byts?) to the end of the file.
        /// </summary>
        /// <param name="backCount"></param>
        internal void SeekItemsBackward(int backCount)
        {
            if (backCount < 0)
            {
                // The caller needs to guarantee that 'backCount' is greater or equals to 0
                throw PSTraceSource.NewArgumentException("backCount");
            }

            if (isRawStream && waitForChanges)
            {
                throw PSTraceSource.NewInvalidOperationException(FileSystemProviderStrings.RawAndWaitCannotCoexist);
            }

            tracer.WriteLine("blocks seek backwards = {0}", backCount);

            ArrayList blocks = new ArrayList();
            if (reader != null)
            {
                // Make the reader automatically detect the encoding
                Seek(0, SeekOrigin.Begin);
                reader.Peek();
                _alreadyDetectEncoding = true;
            }
            Seek(0, SeekOrigin.End);

            if (backCount == 0)
            {
                // If backCount is 0, we should move the position to the end of the file.
                // Maybe the "waitForChanges" is true in this case, which means that we are waiting for new inputs.
                return;
            }

            StringBuilder builder = new StringBuilder();
            foreach (char character in delimiter)
            {
                builder.Insert(0, character);
            }
            string actualDelimiter = builder.ToString();
            long currentBlock = 0;
            string lastDelimiterMatch = null;

            try
            {
                if (isRawStream)
                {
                    // We always read to the end for the raw data.
                    // If it's indicated as RawStream, we move the pointer to the
                    // beginning of the file
                    Seek(0, SeekOrigin.Begin);
                    return;
                }

                for (; currentBlock < backCount; ++currentBlock)
                {
                    if (usingByteEncoding)
                    {
                        if (!ReadByteEncoded(false, blocks, true))
                            break;
                    }
                    else
                    {
                        if (usingDelimiter)
                        {
                            if (!ReadDelimited(false, blocks, true, actualDelimiter))
                                break;
                            // If the delimiter is at the end of the file, we need to read one more
                            // to get to the right position. For example:
                            //      ua123ua456ua -- -Tail 1
                            // If we read backward only once, we get 'ua', and cannot get to the rigth position
                            // So we read one more time, get 'ua456ua', and then we can get the right position
                            lastDelimiterMatch = (string) blocks[0];
                            if (currentBlock == 0 && lastDelimiterMatch.Equals(actualDelimiter, StringComparison.Ordinal))
                                backCount++;
                        }
                        else
                        {
                            if (!ReadByLine(false, blocks, true))
                                break;
                        }
                    }

                    blocks.Clear();
                }

                // If usingByteEncoding is true, we don't create the reader and _backReader
                if (!usingByteEncoding)
                {
                    long curStreamPosition = _backReader.GetCurrentPosition();
                    if (usingDelimiter)
                    {
                        if (currentBlock == backCount)
                        {
                            Dbg.Diagnostics.Assert(lastDelimiterMatch != null, "lastDelimiterMatch should not be null when currentBlock == backCount");
                            if (lastDelimiterMatch.EndsWith(actualDelimiter, StringComparison.Ordinal))
                            {
                                curStreamPosition += _backReader.GetByteCount(this.delimiter);
                            }
                        }
                    }

                    Seek(curStreamPosition, SeekOrigin.Begin);
                }

                tracer.WriteLine("blocks seek position = {0}", stream.Position);
            }
            catch (Exception e)
            {
                if ((e is IOException) ||
                    (e is ArgumentException) ||
                    (e is System.Security.SecurityException) ||
                    (e is UnauthorizedAccessException) ||
                    (e is ArgumentNullException))
                {
                    //Exception contains specific message about the error occured and so no need for errordetails.
                    this.provider.WriteError(new ErrorRecord(e, "GetContentReaderIOError", ErrorCategory.ReadError, path));
                }
                else
                    throw;
            }
        }

        private bool ReadByLine(bool waitChanges, ArrayList blocks, bool readBackward)
        {
            // Reading lines as strings
            string line = readBackward ? _backReader.ReadLine() : reader.ReadLine();

            if (line == null)
            {
                if (waitChanges)
                {
                    // We only wait for changes when read forwards. So here we use reader, instead of 'localReader'
                    do
                    {
                        WaitForChanges(this.path, this.mode, this.access, this.share, reader.CurrentEncoding);
                        line = reader.ReadLine();
                    } while ((line == null) && (!provider.Stopping));
                }
            }

            if (line != null)
                blocks.Add(line);

            int peekResult = readBackward ? _backReader.Peek() : reader.Peek();
            if (peekResult == -1)
                return false;
            else
                return true;
        }

        private bool ReadDelimited(bool waitChanges, ArrayList blocks, bool readBackward, string actualDelimiter)
        {
            // Since the delimiter is a string, we're essentially
            // dealing with a "find the substring" algorithm, but with
            // the additional restriction that we cannot read past the
            // end of the delimiter.  If we read past the end of the delimiter,
            // then we'll eat up bytes that we nede from the filestream.
            // The solution is a modified Boyer-Moore string search algorithm.
            // This version retains the sub-linear search performance (via the 
            // lookup tables,) but offloads much of the dirty work to the
            // very efficient BCL String.IndexOf(, StringComparison.CurrentCulture) method.
            int numRead = 0;
            int currentOffset = actualDelimiter.Length;
            StringBuilder content = new StringBuilder();

            // Populate the offset lookups
            // These will tell us the maximum number of characters
            // we can read to generate another possible match.
            // If we read more characters than this, we risk consuming
            // more of the stream than we need.
            Dictionary<char, int> offsetDictionary = new Dictionary<char, int>();
            foreach (char currentChar in actualDelimiter)
                offsetDictionary[currentChar] = actualDelimiter.Length - actualDelimiter.LastIndexOf(currentChar) - 1;

            do
            {
                if (isRawStream)
                {
                    // when -Raw is used we want to anyway read the whole thing
                    // so avoiding the while loop by reading the entire content.
                    string contentRead = reader.ReadToEnd();
                    numRead = contentRead.Length;
                    content.Append(contentRead);
                }
                else
                {
                    // Read in the required batch of characters
                    var readBuffer = new char[currentOffset];
                    numRead = readBackward
                                  ? _backReader.Read(readBuffer, 0, currentOffset)
                                  : reader.Read(readBuffer, 0, currentOffset);

                    // If we want to wait for changes, then we'll keep on attempting to read
                    // until we fill the buffer.
                    if (numRead == 0)
                    {
                        if (waitChanges)
                        {
                            // But stop reading if the provider is stopping
                            while ((numRead < currentOffset) && (!provider.Stopping))
                            {
                                // Get the change, and try to read more characters
                                // We only wait for changes when read forwards, so here we don't need to check if 'readBackward' is 
                                // true or false, we only use 'reader'. The member 'reader' will be updated by WaitForChanges.
                                WaitForChanges(this.path, this.mode, this.access, this.share, reader.CurrentEncoding);
                                numRead += reader.Read(readBuffer, 0, (currentOffset - numRead));
                            }
                        }
                    }

                    if (numRead > 0)
                    {
                        content.Append(readBuffer, 0, numRead);

                        // Look up the final character in our offset table.
                        // If the character doesn't exist in the lookup table, then it's not in
                        // our search key.  That means the match must happen strictly /after/ the
                        // current position.  Because of that, we can feel confident reading in the
                        // number of characters in the search key, without the risk of reading too many.
                        if (!offsetDictionary.TryGetValue(content[content.Length - 1], out currentOffset))
                            currentOffset = actualDelimiter.Length;

                        // If the final letters matched, then we will get an offset of "0".
                        // In that case, we'll either have a match (and break from the while loop,)
                        // or we need to move the scan forward one position.
                        if (currentOffset == 0)
                            currentOffset = 1;
                    }
                }

                // Two cases where we want to keep reading:
                // 1. Raw stream and we haven't hit the end of file
                // 2. Delimiter not found and we haven't hit the end of file
            } while ((isRawStream && (numRead != 0)) ||
                ((content.ToString().IndexOf(actualDelimiter, StringComparison.Ordinal) < 0) && (numRead != 0)));

            // We've reached the end of file or end of line.  
            if (content.Length > 0)
                blocks.Add(content.ToString());

            int peekResult = readBackward ? _backReader.Peek() : reader.Peek();
            if (peekResult != -1)
                return true;
            else
            {
                if (readBackward && content.Length > 0)
                {
                    return true;
                }
                return false;
            }
        }

        private bool ReadByteEncoded(bool waitChanges, ArrayList blocks, bool readBack)
        {
            if (isRawStream)
            {
                // if RawSteam, read all bytes and return. When RawStream is used, we dont
                // support -first, -last 
                byte[] bytes = new byte[stream.Length];
                int numBytesToRead = (int)stream.Length;
                int numBytesRead = 0;
                while (numBytesToRead > 0)
                {
                    // Read may return anything from 0 to numBytesToRead.
                    int n = stream.Read(bytes, numBytesRead, numBytesToRead);

                    // Break when the end of the file is reached.
                    if (n == 0)
                        break;

                    numBytesRead += n;
                    numBytesToRead -= n;
                }

                if (numBytesRead == 0)
                {
                    return false;
                }
                else
                {
                    blocks.Add(bytes);
                    return true;
                }
            }

            if (readBack)
            {
                if (stream.Position == 0)
                {
                    return false;
                }

                stream.Position--;
                blocks.Add((byte)stream.ReadByte());
                stream.Position--;
                return true;
            }

            // Reading bytes not strings
            int byteRead = stream.ReadByte();

            // We've found the end of the file.
            if (byteRead == -1)
            {
                // If we want to tail the file, wait for
                // the changes
                if (waitChanges)
                {
                    WaitForChanges(this.path, this.mode, this.access, this.share, ClrFacade.GetDefaultEncoding());
                    byteRead = stream.ReadByte();
                }
            }

            // Add the byte we read to the list of blocks
            if (byteRead != -1)
            {
                blocks.Add((byte)byteRead);
                return true;
            }
            else
                return false;
        } // Read

        private void CreateStreams(string filePath, string streamName, FileMode fileMode, FileAccess fileAccess, FileShare fileShare, Encoding fileEncoding)
        {
            // Try to mask off the ReadOnly, and Hidden attributes
            // if they've specified Force.
            if (File.Exists(filePath) && provider.Force)
            {
                // Store the old attributes so that we can recover them
                // in the Close();
                oldAttributes = File.GetAttributes(filePath);
                haveOldAttributes = true;

                // Clear the hidden attribute, and if we're writing, also clear readonly.
                var attributesToClear = FileAttributes.Hidden;
                if ((fileAccess & (FileAccess.Write)) != 0)
                {
                    attributesToClear |= FileAttributes.ReadOnly;
                }
                File.SetAttributes(path, (File.GetAttributes(filePath) & ~attributesToClear));
            }

            // If we want to write to the stream, attempt to open it for reading as well
            // so that we can determine the file encoding as we append to it
            FileAccess requestedAccess = fileAccess;
            if ((fileAccess & (FileAccess.Write)) != 0)
            {
                fileAccess = FileAccess.ReadWrite;
            }

            try
            {
                if (!String.IsNullOrEmpty(streamName))
                {
                    stream = AlternateDataStreamUtilities.CreateFileStream(filePath, streamName, fileMode, fileAccess, fileShare);
                }
                else
                {
                    stream = new FileStream(filePath, fileMode, fileAccess, fileShare);
                }
            }
            catch(IOException)
            {
                if (!String.IsNullOrEmpty(streamName))
                {
                    stream = AlternateDataStreamUtilities.CreateFileStream(filePath, streamName, fileMode, requestedAccess, fileShare);
                }
                else
                {
                    stream = new FileStream(filePath, fileMode, requestedAccess, fileShare);
                }
            }

            if (!usingByteEncoding)
            {
                // Open the reader stream
                if ((fileAccess & (FileAccess.Read)) != 0)
                {
                    reader = new StreamReader(stream, fileEncoding);
                    _backReader = new FileStreamBackReader(stream, fileEncoding);
                }
                
                // Open the writer stream
                if ((fileAccess & (FileAccess.Write)) != 0)
                {
                    // Ensure we are using the proper encoding
                    if ((reader != null) &&
                        ((fileAccess & (FileAccess.Read)) != 0))
                    {
                        reader.Peek();
                        fileEncoding = reader.CurrentEncoding;
                    }

                    writer = new StreamWriter(stream, fileEncoding);
                }
            }
        }

        /// <summary>
        /// Waits for changes to the specified file.  To do this, it closes the file
        /// and then monitors for changes.  Once a change appears, it reopens the streams
        /// and seeks to the last read position.
        /// </summary>
        /// <param name="filePath">The path of the file to read / monitor</param>
        /// <param name="fileMode">The FileMode of the file (ie: Open / Append)</param>
        /// <param name="fileAccess">The access properties of the file (ie: Read / Write)</param>
        /// <param name="fileShare">The sharing properties of the file (ie: Read / ReadWrite)</param>
        /// <param name="fileEncoding">The encoding of the file</param>
        private void WaitForChanges(string filePath, FileMode fileMode, FileAccess fileAccess, FileShare fileShare, Encoding fileEncoding)
        {
            // Close the old stream, and store our current position.
            if (stream != null)
            {
                fileOffset = stream.Position;
                stream.Dispose();
            }

            // Watch for changes, as a blocking call.
            FileInfo watchFile = new FileInfo(filePath);
            long originalLength = watchFile.Length;

            using (FileSystemWatcher watcher = new FileSystemWatcher(watchFile.DirectoryName, watchFile.Name))
            {
                ErrorEventArgs errorEventArgs = null;
                var tcs = new TaskCompletionSource<FileSystemEventArgs>();
                FileSystemEventHandler onChangedHandler = (object source, FileSystemEventArgs e) => { tcs.TrySetResult(e); };
                RenamedEventHandler onRenamedHandler = (object source, RenamedEventArgs e) => { tcs.TrySetResult(e); };
                ErrorEventHandler onErrorHandler = (object source, ErrorEventArgs e) =>
                {
                    errorEventArgs = e;
                    tcs.TrySetResult(new FileSystemEventArgs(WatcherChangeTypes.All, watchFile.DirectoryName, watchFile.Name));
                };

                // With WaitForChanged, we registered for all change types, so we do the same here.
                watcher.Changed += onChangedHandler;
                watcher.Created += onChangedHandler;
                watcher.Deleted += onChangedHandler;
                watcher.Renamed += onRenamedHandler;
                watcher.Error += onErrorHandler;

                try
                {
                    watcher.EnableRaisingEvents = true;

                    while (!provider.Stopping)
                    {
                        bool isTaskCompleted = tcs.Task.Wait(500);

                        if (null != errorEventArgs)
                        {
                            throw errorEventArgs.GetException();
                        }

                        if (isTaskCompleted)
                            break;

                        // If a process is still writing, .NET doesn't generate a change notification.
                        // So do a simple comparison on file size
                        watchFile.Refresh();
                        if (originalLength != watchFile.Length)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    // Done here to guarantee that the handlers are removed prior
                    // to the call to ManualResetEvent's Dispose() method.
                    watcher.EnableRaisingEvents = false;
                    watcher.Changed -= onChangedHandler;
                    watcher.Created -= onChangedHandler;
                    watcher.Deleted -= onChangedHandler;
                    watcher.Renamed -= onRenamedHandler;
                    watcher.Error -= onErrorHandler;
                }
            }

            // Let the change complete.
            // This is a fairly arbitrary number.  Without it, though,
            // some of the filesystem streams cannot be reopened.
            System.Threading.Thread.Sleep(100);

            // Reopen the streams.
            CreateStreams(filePath, null, fileMode, fileAccess, fileShare, fileEncoding);

            // If the file has been shortened, restart from zero.
            // That will let us catch log roll-overs.
            if (fileOffset > stream.Length)
                fileOffset = 0;

            // Seek to the place we last left off.
            stream.Seek(fileOffset, SeekOrigin.Begin);
            if(reader != null) { reader.DiscardBufferedData(); }
            if(_backReader != null) { _backReader.DiscardBufferedData(); }
        }

        /// <summary>
        /// Moves the current stream position in the file
        /// </summary>
        /// 
        /// <param name="offset">
        /// The offset from the origin to move the position to.
        /// </param>
        /// 
        /// <param name="origin">
        /// The origin from which the offset is calculated.
        /// </param>
        /// 
        public void Seek(long offset, SeekOrigin origin)
        {
            if(writer != null) { writer.Flush(); }

            stream.Seek(offset, origin);

            if(writer != null) { writer.Flush(); }
            if(reader != null) { reader.DiscardBufferedData(); }
            if(_backReader != null) { _backReader.DiscardBufferedData(); }
        } // Seek

        /// <summary>
        /// Closes the file.
        /// </summary>
        public void Close()
        {
            bool streamClosed = false;

            if (writer != null)
            {
                try
                {
                    writer.Flush();
                    writer.Dispose();
                }
                finally
                {
                    streamClosed = true;
                }
            }

            if (reader != null)
            {
                reader.Dispose();
                streamClosed = true;
            }

            if (_backReader != null)
            {
                _backReader.Dispose();
                streamClosed = true;
            }

            if (!streamClosed)
            {
                stream.Flush();
                stream.Dispose();
            }

            // Reset the attributes
            if (haveOldAttributes && provider.Force)
            {
                File.SetAttributes(path, oldAttributes);
            }
        } // Close

        /// <summary>
        /// Writes the specified object to the file
        /// </summary>
        /// 
        /// <param name="content">
        /// The objects to write to the file
        /// </param>
        /// 
        /// <returns>
        /// The objects written to the file.
        /// </returns>
        /// 
        public IList Write(IList content)
        {
            foreach (object line in content)
            {
				object[] contentArray = line as object[];
                if (contentArray != null)
                {
                    foreach (object obj in contentArray)
                    {
                        WriteObject(obj);
                    }
                }
                else
                {
                    WriteObject(line);
                }
            }
            return content;
        } // Write
                    
        private void WriteObject(object content)
        {
            if (content == null)
            {
                return;
            }

            if (usingByteEncoding)
            {
                try
                {
                    byte byteToWrite = (byte)content;

                    stream.WriteByte(byteToWrite);
                }
                catch (InvalidCastException)
                {
                    throw PSTraceSource.NewArgumentException("content", FileSystemProviderStrings.ByteEncodingError);
                }
            }
            else
            {
                if (this.suppressNewline)
                {
                    writer.Write(content.ToString());
                }
                else
                {
                    writer.WriteLine(content.ToString());
                }
            }
        } // WriteObject

        /// <summary>
        /// Closes the file stream
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        } // Dispose

        internal void Dispose(bool isDisposing)
        {
            if(isDisposing)
            {
                if(stream != null)
                        stream.Dispose();
                if(reader != null)
                        reader.Dispose();
                if (_backReader != null)
                        _backReader.Dispose();
                if (writer != null)
                        writer.Dispose();
            }
        }

    } // class FileSystemContentStream

    internal sealed class FileStreamBackReader : StreamReader
    {
        internal FileStreamBackReader(FileStream fileStream, Encoding encoding)
            : base(fileStream, encoding)
        {
            _stream = fileStream;
            if (_stream.Length > 0)
            {
                long curPosition = _stream.Position;
                _stream.Seek(0, SeekOrigin.Begin);
                base.Peek();
                _stream.Position = curPosition;
                _currentEncoding = base.CurrentEncoding;
                _currentPosition = _stream.Position;

                // Get the oem encoding and system current ANSI code page
                _oemEncoding = EncodingConversion.Convert(null, EncodingConversion.OEM);
                _defaultAnsiEncoding = EncodingConversion.Convert(null, EncodingConversion.Default);
            }
        }

        private readonly FileStream _stream;
        private readonly Encoding _currentEncoding;
        private readonly Encoding _oemEncoding;
        private readonly Encoding _defaultAnsiEncoding;

        private const int BuffSize = 4096;
        private readonly byte[] _byteBuff = new byte[BuffSize];
        private readonly char[] _charBuff = new char[BuffSize];
        private int _byteCount = 0;
        private int _charCount = 0;
        private long _currentPosition = 0;
        private bool? _singleByteCharSet = null;

        private const byte BothTopBitsSet = 0xC0;
        private const byte TopBitUnset = 0x80; 

        /// <summary>
        /// If the given encoding is OEM or Default, check to see if the code page
        /// is SBCS(single byte character set).
        /// </summary>
        /// <returns></returns>
        private bool IsSingleByteCharacterSet()
        {
            if (_singleByteCharSet != null)
                return (bool)_singleByteCharSet;

            // Porting note: only UTF-8 is supported on Linux, which is not an SBCS
            if ((_currentEncoding.Equals(_oemEncoding) ||
                 _currentEncoding.Equals(_defaultAnsiEncoding))
                && Platform.IsWindows)
            {
                NativeMethods.CPINFO cpInfo;
                if (NativeMethods.GetCPInfo((uint)_currentEncoding.CodePage, out cpInfo) &&
                    cpInfo.MaxCharSize == 1)
                {
                    _singleByteCharSet = true;
                    return true;
                }
            }

            _singleByteCharSet = false;
            return false;
        }

        /// <summary>
        /// We don't support this method because it is not used by the ReadBackward method in FileStreamContentReaderWriter
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="index"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public override int ReadBlock(char[] buffer, int index, int count)
        {
            // This method is not supposed to be used
            throw PSTraceSource.NewNotSupportedException();
        }

        /// <summary>
        /// We don't support this method because it is not used by the ReadBackward method in FileStreamContentReaderWriter
        /// </summary>
        /// <returns></returns>
        public override string ReadToEnd()
        {
            // This method is not supposed to be used
            throw PSTraceSource.NewNotSupportedException();
        }

        /// <summary>
        /// Reset the internal character buffer. Use it only when the position of the internal buffer and
        /// the base stream do not match. These positions can become mismatch when the user read the data
        /// into the buffer and then seek a new position in the underlying stream.
        /// </summary>
        new internal void DiscardBufferedData()
        {
            base.DiscardBufferedData();
            _currentPosition = _stream.Position;
            _charCount = 0;
            _byteCount = 0;
        }

        /// <summary>
        /// Return the current actual stream position 
        /// </summary>
        /// <returns></returns>
        internal long GetCurrentPosition()
        {
            if (_charCount == 0)
                return _currentPosition;

            // _charCount > 0
            int byteCount = _currentEncoding.GetByteCount(_charBuff, 0, _charCount);
            return (_currentPosition + byteCount);
        }

        /// <summary>
        /// Get the number of bytes the delimiter will
        /// be encoded to
        /// </summary>
        /// <param name="delimiter"></param>
        /// <returns></returns>
        internal int GetByteCount(string delimiter)
        {
            char[] chars = delimiter.ToCharArray();
            return _currentEncoding.GetByteCount(chars, 0, chars.Length);
        }

        /// <summary>
        /// Peek the next character
        /// </summary>
        /// <returns>Return -1 if we reach the head of the file</returns>
        public override int Peek()
        {
            if (_charCount == 0)
            {
                if (RefillCharBuffer() == -1)
                {
                    return -1;
                }
            }

            // Return the next available character, but DONT consume it (don't advance the _charCount)
            return (int)_charBuff[_charCount - 1];
        }

        /// <summary>
        /// Read the next character
        /// </summary>
        /// <returns>Return -1 if we reach the head of the file</returns>
        public override int Read()
        {
            if (_charCount == 0)
            {
                if (RefillCharBuffer() == -1)
                {
                    return -1;
                }
            }

            _charCount--;
            return _charBuff[_charCount];
        }

        /// <summary>
        /// Read a specific maximum of characters from the current stream into a buffer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="index"></param>
        /// <param name="count"></param>
        /// <returns>Return the number of characters read, or -1 if we reach the head of the file</returns>
        public override int Read(char[] buffer, int index, int count)
        {
            // deal with the argument validation
            int charRead = 0;

            do
            {
                if (_charCount == 0)
                {
                    if (RefillCharBuffer() == -1)
                    {
                        return charRead;
                    }
                }

                int toRead = _charCount > count ? count : _charCount;

                for (; toRead > 0; toRead--, count--, charRead++)
                {
                    buffer[index++] = _charBuff[--_charCount];
                }

            } while (count > 0);

            return charRead;
        }

        /// <summary>
        /// Read a line from the current stream
        /// </summary>
        /// <returns>Return null if we reach the head of the file</returns>
        public override string ReadLine()
        {
            if (_charCount == 0 && RefillCharBuffer() == -1)
            {
                return null;
            }

            int charsToRemove = 0;
            StringBuilder line = new StringBuilder();

            if (_charBuff[_charCount - 1] == '\r' ||
                _charBuff[_charCount - 1] == '\n')
            {
                charsToRemove++;
                line.Insert(0, _charBuff[--_charCount]);

                if (_charBuff[_charCount] == '\n')
                {
                    if (_charCount == 0 && RefillCharBuffer() == -1)
                    {
                        return string.Empty;
                    }

                    if (_charCount > 0 && _charBuff[_charCount - 1] == '\r')
                    {
                        charsToRemove++;
                        line.Insert(0, _charBuff[--_charCount]);
                    }
                }
            }

            do
            {
                while (_charCount > 0)
                {
                    if (_charBuff[_charCount - 1] == '\r' ||
                        _charBuff[_charCount - 1] == '\n')
                    {
                        line.Remove(line.Length - charsToRemove, charsToRemove);
                        return line.ToString();
                    }
                    else
                    {
                        line.Insert(0, _charBuff[--_charCount]);
                    }
                }

                if (RefillCharBuffer() == -1)
                {
                    line.Remove(line.Length - charsToRemove, charsToRemove);
                    return line.ToString();
                }
            } while (true);
        }

        /// <summary>
        /// Refill the internal character buffer
        /// </summary>
        /// <returns></returns>
        private int RefillCharBuffer()
        {
            if ((RefillByteBuff()) == -1)
            {
                return -1;
            }

            _charCount = _currentEncoding.GetChars(_byteBuff, 0, _byteCount, _charBuff, 0);
            return _charCount;
        }

        /// <summary>
        /// Refill the internal byte buffer
        /// </summary>
        /// <returns></returns>
        private int RefillByteBuff()
        {
            long lengthLeft = _stream.Position;

            if (lengthLeft == 0)
            {
                return -1;
            }

            int toRead = lengthLeft > BuffSize ? BuffSize : (int)lengthLeft;
            _stream.Seek(-toRead, SeekOrigin.Current);

            if (_currentEncoding.Equals(Encoding.UTF8))
            {
                // It's UTF-8, we need to detect the starting byte of a character
                do
                {
                    _currentPosition = _stream.Position;
                    byte curByte = (byte)_stream.ReadByte();
                    if ((curByte & BothTopBitsSet) == BothTopBitsSet ||
                        (curByte & TopBitUnset) == 0x00)
                    {
                        _byteBuff[0] = curByte;
                        _byteCount = 1;
                        break;
                    }
                } while (lengthLeft > _stream.Position);

                if (lengthLeft == _stream.Position)
                {
                    // Cannot find a starting byte. The file is NOT UTF-8 format. Read 'toRead' number of bytes
                    _stream.Seek(-toRead, SeekOrigin.Current);
                    _byteCount = 0;
                }

                _byteCount += _stream.Read(_byteBuff, _byteCount, (int)(lengthLeft - _stream.Position));
                _stream.Position = _currentPosition;
            }
            else if (_currentEncoding.Equals(Encoding.Unicode) ||
                _currentEncoding.Equals(Encoding.BigEndianUnicode) ||
                _currentEncoding.Equals(Encoding.UTF32) ||
                _currentEncoding.Equals(Encoding.ASCII) ||
                IsSingleByteCharacterSet())
            {
                // Unicode -- two bytes per character
                // BigEndianUnicode -- two types per character
                // UTF-32 -- four bytes per character
                // ASCII -- one byte per character
                // The BufferSize will be a multiple of 4, so we can just read toRead number of bytes
                // if the current file is encoded by any of these formatting

                // If IsSignleByteCharacterSet() reutrns true, we are sure that the given encoding is OEM 
                // or Default, and it is SBCS(single byte character set) code page -- one byte per character
                _currentPosition = _stream.Position;
                _byteCount = _stream.Read(_byteBuff, 0, toRead);
                _stream.Position = _currentPosition;
            }
            else
            {
                // OEM and ANSI code pages include multibyte CJK code pages. If the current code page
                // is MBCS(multibyte character set), we cannot detect a starting byte.
                // UTF-7 has some characters encoded into UTF-16 and then in Modified Base64,
                // the start of these characters is indicated by a '+' sign, and the end is
                // indicated by a character that is not in Modified Base64 set.
                // For these encodings, we cannot detect a starting byte with confidence when
                // reading bytes backward. Throw out exception in these cases.
                string errMsg = StringUtil.Format(
                    FileSystemProviderStrings.ReadBackward_Encoding_NotSupport,
                    _currentEncoding.EncodingName);
                throw new BackReaderEncodingNotSupportedException(errMsg, _currentEncoding.EncodingName);
            }

            return _byteCount;
        }

        private static class NativeMethods
        {
            // Default values
            private const int MAX_DEFAULTCHAR = 2;
            private const int MAX_LEADBYTES = 12;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct CPINFO
            {
                [MarshalAs(UnmanagedType.U4)]
                internal int MaxCharSize;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DEFAULTCHAR)]
                public byte[] DefaultChar;

                [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_LEADBYTES)]
                public byte[] LeadBytes;
            };

            /// <summary>
            /// Get information on a named code page
            /// </summary>
            /// <param name="codePage"></param>
            /// <param name="lpCpInfo"></param>
            /// <returns></returns>
            [DllImport(PinvokeDllNames.GetCPInfoDllName, CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool GetCPInfo(uint codePage, out CPINFO lpCpInfo);
        }
    }

    /// <summary>
    /// The exception that indicates the encoding is not supported when reading backward
    /// </summary>
    [SuppressMessage("Microsoft.Usage", "CA2237:MarkISerializableTypesWithSerializable", Justification = "This exception is internal and never thrown by any public API")]
    [SuppressMessage("Microsoft.Design", "CA1032:ImplementStandardExceptionConstructors", Justification = "This exception is internal and never thrown by any public API")]
    internal sealed class BackReaderEncodingNotSupportedException : NotSupportedException
    {
        internal BackReaderEncodingNotSupportedException(string message, string encodingName)
            : base (message)
        {
            _encodingName = encodingName;
        }

        internal BackReaderEncodingNotSupportedException(string encodingName)
        {
            _encodingName = encodingName;
        }

        /// <summary>
        /// Get the encoding name
        /// </summary>
        internal string EncodingName
        {
            get { return _encodingName; }
        }
        private readonly string _encodingName;
    }
} // namespace System.Management.Automation