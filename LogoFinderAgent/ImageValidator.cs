using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public static class ImageValidator
{
   static int tryCount = 0;

   /// <summary>
   /// Validates if the URL resolves to a valid image matching its extension (svg, png, jpg, jpeg)
   /// </summary>
   /// <param name="imageUrl">URL of the image to validate</param>
   /// <returns>True if the URL points to a valid image that matches its extension</returns>
   public static async Task<bool> IsValidImageUrl(string imageUrl)
   {
      Console.Error.Write($"*********** [try #{++tryCount}] *> Validating URL: {imageUrl} *> ");

      try
      {
         // Check if URL is null or empty
         if (string.IsNullOrWhiteSpace(imageUrl))
         {
            Console.Error.WriteLine("❌ URL is empty or null");
            return false;
         }

         // Check if URL has a valid extension
         var fileExtension = Path.GetExtension(imageUrl).ToLower();
         if (string.IsNullOrEmpty(fileExtension))
         {
            Console.Error.WriteLine("❌ URL has no file extension");
            return false;
         }

         // Check if extension is one of the allowed types
         var validExtensions = new[] { ".svg", ".png", ".jpg", ".jpeg" };
         if (!validExtensions.Contains(fileExtension))
         {
            // if fileExtension contains a non-visible character, such as NewLine, replace it with a visible unicode char
            fileExtension = fileExtension.Replace("\n", "⏎").Replace("\r", "⏎");
            // if fileExtension contains a non-visible character, such as Tab, replace it with a visible unicode char
            fileExtension = fileExtension.Replace("\t", "⇥");
            // if fileExtension contains a non-visible character, such as Space, replace it with a visible unicode char
            fileExtension = fileExtension.Replace(" ", "␣");

            Console.Error.WriteLine($"❌ Invalid file extension: {fileExtension}");
            return false;
         }

         // Retrieve URL data to local storage as a temp file
         // Pretend to be Chrome on Windows
         using var httpClient = new HttpClient();
         httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
             "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
             "AppleWebKit/537.36 (KHTML, like Gecko) " +
             "Chrome/125.0 Safari/537.36");
         var response = await httpClient.GetAsync(imageUrl);

         // If not successful (including 404), return false
         if (!response.IsSuccessStatusCode)
         {
            Console.Error.WriteLine($"❌ HTTP request failed: {response.StatusCode}");
            return false;
         }

         var contentBytes = await response.Content.ReadAsByteArrayAsync();
         var tempFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
         await File.WriteAllBytesAsync(tempFile, contentBytes);

         try
         {
            // Validate file content matches extension
            switch (fileExtension)
            {
               case ".svg":
                  return IsValidSvg(tempFile);
               case ".png":
                  return IsValidPng(tempFile);
               case ".jpg":
               case ".jpeg":
                  return IsValidJpeg(tempFile);
               default:
                  return false;
            }
         }
         finally
         {
            // Clean up temp file
            if (File.Exists(tempFile))
            {
               File.Delete(tempFile);
            }
         }
      }
      catch (Exception ex)
      {
         Console.Error.WriteLine($"❌ Error validating image: {ex.Message}");
         return false;
      }
   }

   private static bool IsValidPng(string filePath)
   {
      try
      {
         // PNG signature: 89 50 4E 47 0D 0A 1A 0A
         var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
         using var fileStream = File.OpenRead(filePath);
         var fileHeader = new byte[8];
         if (fileStream.Read(fileHeader, 0, 8) != 8)
         {
            Console.Error.WriteLine("❌ Could not read PNG header");
            return false;
         }

         if (!fileHeader.SequenceEqual(pngHeader))
         {
            Console.Error.WriteLine("❌ Invalid PNG format");
            return false;
         }

         Console.Error.WriteLine("✅ Valid PNG image");
         return true;
      }
      catch (Exception ex)
      {
         Console.Error.WriteLine($"❌ PNG validation error: {ex.Message}");
         return false;
      }
   }

   private static bool IsValidJpeg(string filePath)
   {
      try
      {
         // JPEG starts with FF D8 and ends with FF D9
         using var fileStream = File.OpenRead(filePath);
         var fileHeader = new byte[2];
         if (fileStream.Read(fileHeader, 0, 2) != 2)
         {
            Console.Error.WriteLine("❌ Could not read JPEG header");
            return false;
         }

         if (fileHeader[0] != 0xFF || fileHeader[1] != 0xD8)
         {
            Console.Error.WriteLine("❌ Invalid JPEG format");
            return false;
         }

         // Check ending bytes
         fileStream.Seek(-2, SeekOrigin.End);
         var fileFooter = new byte[2];
         if (fileStream.Read(fileFooter, 0, 2) != 2)
         {
            Console.Error.WriteLine("❌ Could not read JPEG footer");
            return false;
         }

         if (fileFooter[0] != 0xFF || fileFooter[1] != 0xD9)
         {
            Console.Error.WriteLine("❌ Invalid JPEG format");
            return false;
         }

         Console.Error.WriteLine("✅ Valid JPEG image");
         return true;
      }
      catch (Exception ex)
      {
         Console.Error.WriteLine($"❌ JPEG validation error: {ex.Message}");
         return false;
      }
   }

   private static bool IsValidSvg(string filePath)
   {
      try
      {
         // Read the beginning of the file to check for SVG signature
         string fileContent = File.ReadAllText(filePath);
         string lowerContent = fileContent.ToLower().Trim();

         // Check if it contains proper SVG tags
         if (lowerContent.Contains("<svg") &&
             (lowerContent.Contains("xmlns=\"http://www.w3.org/2000/svg\"") ||
              lowerContent.Contains("xmlns='http://www.w3.org/2000/svg'")))
         {
            Console.Error.WriteLine("✅ Valid SVG image");
            return true;
         }

         Console.Error.WriteLine("❌ Invalid SVG format");
         return false;
      }
      catch (Exception ex)
      {
         Console.Error.WriteLine($"❌ SVG validation error: {ex.Message}");
         return false;
      }
   }
}