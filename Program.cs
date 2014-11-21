﻿using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;
using System.Threading;
using System.Diagnostics;
using ICSharpCode.SharpZipLib.BZip2;
using NDesk.Options;

namespace RSCacheTool
{
	static class Program
	{
		static string cacheDir = Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\jagexcache\runescape\LIVE\");
		static string outDir = "cache\\";

		static int Main(string[] args)
		{
			bool error = false;

			bool help = false, extract = false, combine = false, combineOverwrite = false, combineMergeIncomplete = false;
			int extractArchive = -1, combineArchive = 40, combineStartFile = 0;

			OptionSet argsParser = new OptionSet() {
				{ "h", "show this message", val => { help = true; } },

				{ "e", "extract files from cache", val => { extract = true; } },
				{ "a=", "single archive to extract, if not given all archives will be extracted", val => { extractArchive = Convert.ToInt32(val); } },

				{ "c", "combine sound", val => { combine = true; } },
				{ "s=", "archive to combine sounds of, defaults to 40", val => { combineArchive = Convert.ToInt32(val); } },
				{ "o", "overwrite existing soundfiles", val => { combineOverwrite = true; } },
				{ "i", "merge incomplete files (into special directory)", val => { combineMergeIncomplete = true; } },
			};

			List<string> otherArgs = argsParser.Parse(args);

			for (int i = 0; i < otherArgs.Count; i++)
			{
				string parsedPath = otherArgs[i];
				if (!parsedPath.EndsWith("\\"))
					parsedPath += "\\";

				parsedPath = Environment.ExpandEnvironmentVariables(parsedPath);

				if (Directory.Exists(parsedPath))
				{
					if (i == 0)
						outDir = parsedPath;
					else if (i == 1)
						cacheDir = parsedPath;
				}
				else
				{
					Console.WriteLine("The directory: " + parsedPath + " is not valid.");
					error = true;
				}
			}

			if (args.Length == 0 || help)
			{
				Console.WriteLine(
					"Usage: rscachetools [options] outDir cacheDir\n" + 
					"Provides various tools for extracting and manipulating RuneScape's cache files.\n" +
					"\n" +
					"Arguments:\n" +
					"outDir - The directory in which all files generated by this tool will be placed. Default: cache\\\n" +
					"cacheDir - The directory that contains all cache files. Default: %USERPROFILE%\\jagexcache\\runescape\\LIVE\\.\n" +
					"\n" +
					"Options:"
				);

				argsParser.WriteOptionDescriptions(Console.Out);
			}
			else if (!error)
			{
				if (extract)
					ExtractFiles(extractArchive);

				if (combine)
					CombineSounds(combineArchive, combineStartFile, combineOverwrite, combineMergeIncomplete);
			}

			return 0;
		}

		/// <summary>
		/// Rips all files from the cachefile and puts them (structured and given a fitting extension where possible) in the fileDir.
		/// </summary>
		static void ExtractFiles(int archive)
		{
			int startArchive = 0, endArchive = 255;

			if (archive != -1)
			{
				startArchive = archive;
				endArchive = archive;
			}

			using (FileStream cacheFile = File.Open(cacheDir + "main_file_cache.dat2", FileMode.Open, FileAccess.Read))
			{
				for (int archiveIndex = startArchive; archiveIndex <= endArchive; archiveIndex++)
				{
					string indexFileString = cacheDir + "main_file_cache.idx" + archiveIndex.ToString();

					if (File.Exists(indexFileString))
					{
						FileStream indexFile = File.Open(indexFileString, FileMode.Open, FileAccess.Read);

						int fileCount = (int)indexFile.Length / 6;

						for (int fileIndex = 0; fileIndex < fileCount; fileIndex++)
						{
							bool fileError = false;

							indexFile.Seek(fileIndex * 6, SeekOrigin.Begin);

							uint fileSize = indexFile.ReadBytes(3);
							long startChunkOffset = indexFile.ReadBytes(3) * 520L;

							//Console.WriteLine("New file: archive: {0} file: {1} offset: {3} size: {2}", archiveIndex, fileIndex, fileSize, startChunkOffset);
 
							if (fileSize > 0 && startChunkOffset > 0 && startChunkOffset + fileSize <= cacheFile.Length)
							{
								byte[] buffer = new byte[fileSize];
								int writeOffset = 0;
								long currentChunkOffset = startChunkOffset;

								for (int chunkIndex = 0; writeOffset < fileSize && currentChunkOffset > 0; chunkIndex++)
								{
									cacheFile.Seek(currentChunkOffset, SeekOrigin.Begin);

									int chunkSize;
									int checksumFileIndex = 0;

									if (fileIndex < 65536)
									{
										chunkSize = (int)Math.Min(512, fileSize - writeOffset);
									}
									else
									{
										//if file index exceeds 2 bytes, add 65536 and read 2(?) extra bytes
										chunkSize = (int)Math.Min(510, fileSize - writeOffset);

										cacheFile.ReadByte();
										checksumFileIndex = (cacheFile.ReadByte() << 16);
									}

									checksumFileIndex += (int)cacheFile.ReadBytes(2);
									int checksumChunkIndex = (int)cacheFile.ReadBytes(2);
									long nextChunkOffset = cacheFile.ReadBytes(3) * 520L;
									int checksumArchiveIndex = cacheFile.ReadByte();

									//Console.WriteLine("Chunk {2}: archive: {3} file: {1} size: {0} nextoffset: {4}", chunkSize, checksumFileIndex, checksumChunkIndex, checksumArchiveIndex, nextChunkOffset);

									if (checksumFileIndex == fileIndex && checksumChunkIndex == chunkIndex && checksumArchiveIndex == archiveIndex &&
										nextChunkOffset >= 0 && nextChunkOffset < cacheFile.Length)
									{
										cacheFile.Read(buffer, writeOffset, chunkSize);
										writeOffset += chunkSize;
										currentChunkOffset = nextChunkOffset;
									}
									else
									{
										Console.WriteLine("Ignoring file because a chunk's checksum doesn't match, ideally should not happen.");

										fileError = true;
										break;
									}
								}

								if (!fileError)
								{
									//process file
									string outFileDir = outDir + archiveIndex + "\\";
									string outFileName = fileIndex.ToString();
									byte[] tempBuffer;

									//decompress gzip
									if (buffer.Length > 10 && (buffer[9] << 8) + buffer[10] == 0x1f8b) //gzip
									{
										//remove the first 9 bytes cause they seem to be descriptors of sorts (no idea what they do but they are not part of the file)
										tempBuffer = new byte[fileSize - 9];
										Array.Copy(buffer, 9, tempBuffer, 0, fileSize - 9);
										buffer = tempBuffer;

										GZipStream decompressionStream = new GZipStream(new MemoryStream(buffer), CompressionMode.Decompress);

										int readBytes;
										tempBuffer = new byte[0];

										do
										{
											byte[] readBuffer = new byte[100000];
											readBytes = decompressionStream.Read(readBuffer, 0, 100000);

											int storedBytes = tempBuffer.Length;
											Array.Resize(ref tempBuffer, tempBuffer.Length + readBytes);
											Array.Copy(readBuffer, 0, tempBuffer, storedBytes, readBytes);
										}
										while (readBytes == 100000);

										buffer = tempBuffer;

										Console.WriteLine("File decompressed as gzip.");
									}

									//decompress bzip2
									if (buffer.Length > 14 && buffer[9] == 0x31 && buffer[10] == 0x41 && buffer[11] == 0x59 && buffer[12] == 0x26 && buffer[13] == 0x53 && buffer[14] == 0x59) //bzip2
									{
										//remove the first 9 bytes cause they seem to be descriptors of sorts (no idea what they do but they are not part of the file)
										tempBuffer = new byte[fileSize - 9];
										Array.Copy(buffer, 9, tempBuffer, 0, fileSize - 9);
										buffer = tempBuffer;

										//prepend file header
										byte[] magic = new byte[] {
											0x42, 0x5a, //BZ (signature)
											0x68,		//h (version)
											0x31		//*100kB block-size
										};

										tempBuffer = new byte[magic.Length + buffer.Length];
										magic.CopyTo(tempBuffer, 0);
										buffer.CopyTo(tempBuffer, magic.Length);
										buffer = tempBuffer;

										BZip2InputStream decompressionStream = new BZip2InputStream(new MemoryStream(buffer));

										int readBytes;
										tempBuffer = new byte[0];

										do
										{
											byte[] readBuffer = new byte[100000];
											readBytes = decompressionStream.Read(readBuffer, 0, 100000);

											int storedBytes = tempBuffer.Length;
											Array.Resize(ref tempBuffer, tempBuffer.Length + readBytes);
											Array.Copy(readBuffer, 0, tempBuffer, storedBytes, readBytes);
										}
										while (readBytes == 100000);

										buffer = tempBuffer;

										Console.WriteLine("File decompressed as bzip2.");														
									}

									//detect ogg: OggS
									if (buffer.Length > 3 && (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3] == 0x4f676753)
										outFileName += ".ogg";

									//detect ogg: 5 bytes - OggS
									if (buffer.Length > 8 && (buffer[5] << 24) + (buffer[6] << 16) + (buffer[7] << 8) + buffer[8] == 0x4f676753)
									{
										tempBuffer = new byte[fileSize - 5];
										Array.Copy(buffer, 5, tempBuffer, 0, fileSize - 5);
										buffer = tempBuffer;

										outFileName += ".ogg";
									}

									//detect jag: JAGA
									if (buffer.Length > 3 && (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3] == 0x4a414741)
										outFileName += ".jaga";

									//detect jag: 5 bytes - JAGA
									if (buffer.Length > 8 && (buffer[5] << 24) + (buffer[6] << 16) + (buffer[7] << 8) + buffer[8] == 0x4a414741)
									{
										tempBuffer = new byte[fileSize - 5];
										Array.Copy(buffer, 5, tempBuffer, 0, fileSize - 5);
										buffer = tempBuffer;

										outFileName += ".jaga";
									}

									//detect png: .PNG
									if (buffer.Length > 3 && (uint)(buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3] == 0x89504e47)
										outFileName += ".png";

									//detect png: 5 bytes - .PNG
									if (buffer.Length > 8 && (uint)(buffer[5] << 24) + (buffer[6] << 16) + (buffer[7] << 8) + buffer[8] == 0x89504e47)
									{
										tempBuffer = new byte[fileSize - 5];
										Array.Copy(buffer, 5, tempBuffer, 0, fileSize - 5);
										buffer = tempBuffer;

										outFileName += ".png";
									}

									//create and write file
									if (!Directory.Exists(outFileDir))
										Directory.CreateDirectory(outFileDir);

									using (FileStream outFile = File.Open(outFileDir + outFileName, FileMode.Create, FileAccess.Write))
									{
										outFile.Write(buffer, 0, buffer.Length);
										Console.WriteLine(outFileDir + outFileName);
									}
								}
							}
							else
							{
								Console.WriteLine("Ignoring file because of size or offset.");
							}
						}
					}
				}
			}

			Console.WriteLine("Done extracting files.");
		}

		/// <summary>
		/// Combines the sound files (.jag &amp; .ogg) in the specified archive (40 for the build it was made on), and puts them into the soundtracks directory.
		/// </summary>
		static void CombineSounds(int archive = 40, int startFile = 0, bool overwriteExisting = false, bool mergeIncomplete = false)
		{
			string archiveDir = outDir + archive + "\\";
			string soundDir = outDir + "sound\\";

			//gather all index files
			string[] indexFiles = Directory.GetFiles(archiveDir, "*.jag", SearchOption.TopDirectoryOnly);

			//create directories
			if (!Directory.Exists(soundDir + "incomplete\\"))
				Directory.CreateDirectory(soundDir + "incomplete\\");

			int i = 0;
			foreach (string indexFileString in indexFiles)
			{
				if (i < startFile)
				{
					i++;
					continue;
				}

				bool incomplete = false;
				List<string> chunkFiles = new List<string>();

				using (FileStream indexFileStream = File.Open(indexFileString, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					indexFileStream.Seek(32, SeekOrigin.Begin);

					while (indexFileStream.ReadBytes(4) != 0x4f676753)
					{
						uint fileId = indexFileStream.ReadBytes(4);

						//check if the file exists and add it to the buffer if it does
						if (File.Exists(archiveDir + fileId + ".ogg"))
							chunkFiles.Add(archiveDir + fileId + ".ogg");
						else
							incomplete = true;
					}

					//copy the first chunk to a temp file so SoX can handle the combining
					indexFileStream.Seek(-4, SeekOrigin.Current);

					//wait till file is available
					while (true)
					{
						try
						{
							using (FileStream tempIndexFile = File.Open("~index.ogg", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
							{
								indexFileStream.CopyTo(tempIndexFile);
								break;
							}
						}
						catch (IOException)
						{
							Thread.Sleep(200);
						}
					}
				}

				if (!incomplete || incomplete && mergeIncomplete)
				{
					string outFile = soundDir + (incomplete ? "incomplete\\" : "") + i + ".ogg";

					if (!overwriteExisting && File.Exists(outFile))
						Console.WriteLine("Skipping track because it already exists.");
					else
					{
						//combine the files with sox
						Console.WriteLine("Running SoX to concatenate ogg audio chunks.");

						Process soxProcess = new Process();
						soxProcess.StartInfo.FileName = "sox.exe";

						soxProcess.StartInfo.Arguments = "--combine concatenate ~index.ogg";
						chunkFiles.ForEach((str) =>
						{
							soxProcess.StartInfo.Arguments += " " + str;
						});
						soxProcess.StartInfo.Arguments += " " + soundDir + "incomplete\\" + i + ".ogg ";
						soxProcess.StartInfo.UseShellExecute = false;

						soxProcess.Start();
						soxProcess.WaitForExit();

						if (soxProcess.ExitCode == 0)
						{
							if (!incomplete)
							{
								//clear space
								if (File.Exists(soundDir + i + ".ogg"))
									File.Delete(soundDir + i + ".ogg");

								File.Move(soundDir + "incomplete\\" + i + ".ogg", soundDir + i + ".ogg");
							}

							Console.WriteLine(soundDir + (incomplete ? "incomplete\\" : "") + i + ".ogg");
						}
						else
							Console.WriteLine("SoX encountered error code " + soxProcess.ExitCode + " and probably didn't finish processing the files.");
					}
				}
				else
					Console.WriteLine("Skipping track because it's incomplete.");

				i++;
			}

			//cleanup on isle 4
			File.Delete("~index.ogg");

			Console.WriteLine("Done combining sound.");
		}

		/// <summary>
		/// Reads a given amount of bytes from the stream.
		/// </summary>
		public static uint ReadBytes(this Stream stream, byte bytes)
		{
			if (bytes == 0 || bytes > 4)
				throw new ArgumentOutOfRangeException();

			uint result = 0;

			for (int i = 0; i < bytes; i++)
				result += (uint)stream.ReadByte() << (bytes - i - 1) * 8;

			return result;
		}
	}
}
