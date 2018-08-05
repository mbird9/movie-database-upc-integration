using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using System.Web.Script.Serialization;
using MvcMovie.Models;
using Newtonsoft.Json;

namespace MvcMovie.Controllers
{
    public class MoviesController : Controller
    {
        private MovieDBContext db = new MovieDBContext();
        private HttpClient client = new HttpClient();

        // GET: Movies
        public ActionResult Index(string movieGenre, string searchString)
        {
            var GenreLst = new List<string>();

            var GenreQry = from d in db.Movies
                           orderby d.Genre
                           select d.Genre;

            GenreLst.AddRange(GenreQry.Distinct());
            ViewBag.movieGenre = new SelectList(GenreLst);

            var movies = from m in db.Movies
                         select m;

            if (!String.IsNullOrEmpty(searchString))
            {
                movies = movies.Where(s => s.Title.Contains(searchString));
            }

            if (!String.IsNullOrEmpty(movieGenre))
            {
                movies = movies.Where(x => x.Genre == movieGenre);
            }

            return View(movies);
        }

        // GET: Movies/Details/5
        public ActionResult Details(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Movie movie = db.Movies.Find(id);
            if (movie == null)
            {
                return HttpNotFound();
            }
            return View(movie);
        }

        // GET: Movies/Create
        public ActionResult Create()
        {
            return View();
        }

        // POST: Movies/Create
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Create([Bind(Include = "ID,Title,ReleaseDate,Genre,Price,Rating")] Movie movie)
        {
            if (ModelState.IsValid)
            {
                db.Movies.Add(movie);
                db.SaveChanges();
                return RedirectToAction("Index");
            }

            return View(movie);
        }

        // GET: Movies/FindByUPC
        public ActionResult FindByUPC()
        {
            return View();
        }

        // POST: Movies/FindByUPC
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult FindByUPC(string UPC)
        {
            var httpWebRequest = (HttpWebRequest)WebRequest.Create("https://api.upcitemdb.com/prod/trial/lookup");
            httpWebRequest.ContentType = "application/json";
            httpWebRequest.Method = "POST";

            using (var streamWriter = new StreamWriter(httpWebRequest.GetRequestStream()))
            {
                //Serialize the UPC 
                string json = new JavaScriptSerializer().Serialize(new
                {
                    upc = UPC,
                });
                streamWriter.Write(json);
            }
            try {
                var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();

                string result;

                using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                {
                    result = streamReader.ReadToEnd();
                }

                DataSet returnedUPCData = ConvertJSONStringtoDataSet(result);

                //Retrieve the title from the DataSet (only one record is ever returned in the table so there is currently no need to query)
                string movieTitle = Convert.ToString(returnedUPCData.Tables[1].Rows[0]["title"]);

                //Clean up returned title 
                string updatedTitle = UpdateTitleFormatting(movieTitle);

                //update the movie title to be URL-friendly
                string movieTitleURL = updatedTitle.Replace(" ", "+");

                DataSet returnedDataSet = FindMatches(movieTitleURL);
                DataTable matches = returnedDataSet.Tables["Search"];

                //query the matches dataset to only return items of Type "movie" with the exact movie title
                var query =
                    from match in matches.AsEnumerable()
                    where match.Field<string>("Type") == "movie"
                        && match.Field<string>("Title") == updatedTitle
                    select new
                    {
                        Title = match.Field<string>("Title"),
                        Year = match.Field<string>("Year"),
                    };

                foreach (var movieMatch in query)
                {
                    if (query.Count() == 1)
                    {
                        //only one record exists, make a call back to the OMDb API to get the full movie information
                        var movieDataSet = FindMatches(movieMatch.Title, movieMatch.Year);

                        DataTable movieInfo = movieDataSet.Tables["rootNode"];

                        Movie movie = new Movie
                        {
                            Title = movieInfo.Rows[0]["Title"].ToString(),
                            ReleaseDate = Convert.ToDateTime(movieInfo.Rows[0]["Released"]),
                            Genre = movieInfo.Rows[0]["Genre"].ToString(),
                            Rating = movieInfo.Rows[0]["Rated"].ToString(),
                            Price = 9.99M, //TODO: Get price from api.upcitemdb.com or remove the field entirely
                            UPC = UPC
                        };
                        return Create(movie);
                    }
                    else
                    {
                        //TODO: Add ability for user to select from the list of returned movies the one that matches their scanned movie
                    }
                }
            }
            catch (WebException e)
            {
                //TODO: Add functionality to tell the user that they did not input a valid UPC
            }

            return View();
        }

        /// <summary>
        /// Deserializes JSON to XML, then converts it to a DataSet
        /// </summary>
        /// <param name="jsonString"></param>
        /// <returns></returns>
        private static DataSet ConvertJSONStringtoDataSet(String jsonString)
        {
            // Using XML as an interface of deserializing
            var xmlDoc = new System.Xml.XmlDocument();

            // Note:Json convertor needs a json with one node as root
            jsonString = "{ \"rootNode\": {" + jsonString.Trim().TrimStart('{').TrimEnd('}') + @"} }";
            // Now it is secure that we have always a Json with one node as root 
            xmlDoc = JsonConvert.DeserializeXmlNode(jsonString);

            // DataSet is able to read from XML and return a proper DataSet
            var result = new DataSet();
            result.ReadXml(new System.Xml.XmlNodeReader(xmlDoc));
            return result;
        }

        /// <summary>
        /// Cleans up returned title from UPCitemdb to be used in the request to OMDb 
        /// </summary>
        /// <param name="title">Title returned from UPCitemdb</param>
        /// <returns></returns>
        private static string UpdateTitleFormatting(string title)
        {
            TextInfo textInfo = new CultureInfo("en-US", false).TextInfo;

            //Use a regular expression to remove unneccessary info from title
            //NOTE: Currently strips only matches to "dvd", "blu-ray", and "disc" found within parentheses (and the parentheses themselves)
            string regex = @"\(([^)]*[^a-zA-Z)])?(dvd|blu-ray|disc)([^a-zA-Z)][^)]*)?\)";
            string newTitle = Regex.Replace(title.ToLower(), regex, "");

            //Set the words back to title case, and remove any leading or ending spaces
            newTitle = textInfo.ToTitleCase(newTitle);
            newTitle = newTitle.Trim();

            return newTitle;
        }

        /// <summary>
        /// Makes a request to the OMDb API and returns either a list of search results, or the full information of a movie (if year is supplied)
        /// </summary>
        /// <param name="title">Movie title </param>
        /// <param name="year">Movie year; Optional </param>
        /// <returns></returns>
        private static DataSet FindMatches(string title, string year="")
        {
            string url;

            if (year == "")
            {
                //year is not used during a general search
                url = "http://www.omdbapi.com/?s=" + title + "&apikey=3c15edec";
            }
            else
            {
                url = "http://www.omdbapi.com/?t=" + title + "&y" + year + "&apikey=3c15edec";
            }
            var httpWebRequest = (HttpWebRequest)WebRequest.Create(url);
            httpWebRequest.ContentType = "application/json";
            httpWebRequest.Method = "GET";

            var httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            string result;

            using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
            {
                result = streamReader.ReadToEnd();
            }

            var resultDataSet = ConvertJSONStringtoDataSet(result);
            return resultDataSet;
        }

        // GET: Movies/Edit/5
        public ActionResult Edit(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Movie movie = db.Movies.Find(id);
            if (movie == null)
            {
                return HttpNotFound();
            }
            return View(movie);
        }

        // POST: Movies/Edit/5
        // To protect from overposting attacks, please enable the specific properties you want to bind to, for 
        // more details see https://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Edit([Bind(Include = "ID,Title,ReleaseDate,Genre,Price,Rating")] Movie movie)
        {
            if (ModelState.IsValid)
            {
                db.Entry(movie).State = EntityState.Modified;
                db.SaveChanges();
                return RedirectToAction("Index");
            }
            return View(movie);
        }

        // GET: Movies/Delete/5
        public ActionResult Delete(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            Movie movie = db.Movies.Find(id);
            if (movie == null)
            {
                return HttpNotFound();
            }
            return View(movie);
        }

        // POST: Movies/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public ActionResult DeleteConfirmed(int id)
        {
            Movie movie = db.Movies.Find(id);
            db.Movies.Remove(movie);
            db.SaveChanges();
            return RedirectToAction("Index");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                db.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
